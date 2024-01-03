﻿using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Logging;
using NAPS2.Images.Bitwise;
using NAPS2.Ocr;
using NAPS2.Pdf.Pdfium;
using NAPS2.Scan;
using PdfSharpCore.Drawing;
using PdfSharpCore.Drawing.Layout;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using PdfSharpCore.Pdf.Security;
using PdfDocument = PdfSharpCore.Pdf.PdfDocument;
using PdfPage = PdfSharpCore.Pdf.PdfPage;

namespace NAPS2.Pdf;

/// <summary>
/// Exports images to a PDF file.
/// </summary>
public class PdfExporter
{
    private readonly ScanningContext _scanningContext;
    private readonly ILogger _logger;

    public PdfExporter(ScanningContext scanningContext)
    {
        _scanningContext = scanningContext;
        _logger = scanningContext.Logger;
    }

    public Task<bool> Export(string path, ICollection<ProcessedImage> images,
        PdfExportParams? exportParams = null, OcrParams? ocrParams = null, ProgressHandler progress = default)
        => Export(new OutputPathOrStream(path, null), images, exportParams, ocrParams, progress);

    public Task<bool> Export(Stream stream, ICollection<ProcessedImage> images,
        PdfExportParams? exportParams = null, OcrParams? ocrParams = null, ProgressHandler progress = default)
        => Export(new OutputPathOrStream(null, stream), images, exportParams, ocrParams, progress);

    private async Task<bool> Export(OutputPathOrStream output, ICollection<ProcessedImage> images,
        PdfExportParams? exportParams = null, OcrParams? ocrParams = null, ProgressHandler progress = default)
    {
        return await Task.Run(async () =>
        {
            // The current iteration of PDF export is fairly complicated. We do a hybrid export using both PdfSharp
            // and Pdfium. "Simple" exports just use PdfSharp. If we have imported PDF pages that are stored as PDFs
            // (i.e. they weren't generated by NAPS2 and can't be extracted to a single image), we use Pdfium to:
            // 1. Check if the pages have text (and therefore are ineligible for OCR).
            // 2. Add those "passthrough" pages to the final PDF file (under certain conditions).
            // For context PdfSharp has a number of bugs with handling arbitrary PDFs, so using Pdfium for exporting
            // those PDF pages lets us avoid those bugs (given we also use Pdfium for importing).
            //
            // Export is also complicated by the fact that we may or may not have OCR enabled, and when it is enabled,
            // we want to parallelize and pipeline the different operations (image rendering, OCR, PDF saving) to
            // maximize performance.
            //
            // It would be simpler if we could use Pdfium for everything, but it doesn't support a lot of features we
            // need, e.g. configuring interpolation, encryption, PDF/A, etc.

            exportParams ??= new PdfExportParams();
            var document = InitializeDocument(exportParams);

            // TODO: Consider storing text from imported image-based pages in PostProcessingData so it can be saved even
            // when not exporting with OCR (assuming no transforms). 
            var ocrEngine = GetOcrEngine(ocrParams);

            var imagePages = new List<PageExportState>();
            var pdfPages = new List<PageExportState>();

            int currentProgress = 0;

            void IncrementProgress()
            {
                Interlocked.Increment(ref currentProgress);
                progress.Report(currentProgress, images.Count);
            }

            progress.Report(0, images.Count);

            try
            {
                int pageIndex = 0;
                foreach (var image in images)
                {
                    var pageState = new PageExportState(
                        image, pageIndex++, document, document.AddPage(), ocrEngine, ocrParams, IncrementProgress,
                        progress.CancelToken, exportParams.Compat);
                    // TODO: To improve our ability to passthrough, we could consider using Pdfium to apply the transform to
                    // the underlying PDF file. For example, doing color shifting on individual text + image objects, or
                    // applying matrix changes.
                    // TODO: We also can consider doing this even for scanned image transforms - e.g. for deskew, maybe
                    // rather than rasterize that, rely on the pdf to do the skew transform, which should render better at
                    // different scaling.
                    if (IsPdfStorage(image.Storage) && image.TransformState == TransformState.Empty)
                    {
                        pdfPages.Add(pageState);
                    }
                    else
                    {
                        imagePages.Add(pageState);
                    }
                }

                var imagePagesPipeline = ocrEngine != null
                    ? Pipeline.For(imagePages)
                        .Step(RenderStep)
                        .Step(InitOcrStep)
                        .Step(WaitForOcrStep)
                        .Step(WriteToPdfSharpStep)
                        .Run()
                    : Pipeline.For(imagePages)
                        .Step(RenderStep)
                        .Step(WriteToPdfSharpStep)
                        .Run();

                var pdfPagesPrePipeline = ocrEngine != null
                    ? Pipeline.For(pdfPages).Step(CheckIfOcrNeededStep).Run()
                    : Task.FromResult(pdfPages);

                await pdfPagesPrePipeline;

                var pdfPagesOcrPipeline = Pipeline.For(pdfPages.Where(x => x.NeedsOcr))
                    .Step(RenderStep)
                    .Step(InitOcrStep)
                    .Step(WaitForOcrStep)
                    .Run();

                await imagePagesPipeline;
                await pdfPagesOcrPipeline;
                if (progress.IsCancellationRequested) return false;

                // TODO: Doing in memory as that's presumably faster than IO, but of course that's quite a bit of memory use potentially...
                var stream = FinalizeAndSaveDocument(document, exportParams);
                if (progress.IsCancellationRequested) return false;

                return MergePassthroughPages(stream, output, pdfPages, exportParams, progress);
            }
            finally
            {
                // We can't use a DisposableList as the objects we need to dispose are generated on the fly
                foreach (var state in imagePages.Concat(pdfPages))
                {
                    state.Embedder?.Dispose();
                }
            }
        });
    }

    private bool MergePassthroughPages(MemoryStream stream, OutputPathOrStream output, List<PageExportState> passthroughPages,
        PdfExportParams exportParams, ProgressHandler progress)
    {
        if (!passthroughPages.Any())
        {
            output.CopyFromStream(stream);
            return true;
        }
        // TODO: Should we do this (or maybe the whole pdf export/import) in a worker to avoid contention?
        // TODO: Although we would need to be careful to handle OcrRequestQueue state correctly across processes.
        lock (PdfiumNativeLibrary.Instance)
        {
            var destBuffer = stream.GetBuffer();
            var destHandle = GCHandle.Alloc(destBuffer, GCHandleType.Pinned);
            try
            {
                var password = exportParams.Encryption.EncryptPdf ? exportParams.Encryption.OwnerPassword : null;
                using var destDoc =
                    Pdfium.PdfDocument.Load(destHandle.AddrOfPinnedObject(), (int) stream.Length, password);
                foreach (var state in passthroughPages)
                {
                    destDoc.DeletePage(state.PageIndex);
                    if (state.Image.Storage is ImageFileStorage fileStorage)
                    {
                        using var sourceDoc = Pdfium.PdfDocument.Load(fileStorage.FullPath);
                        CopyPage(destDoc, sourceDoc, state);
                    }
                    else if (state.Image.Storage is ImageMemoryStorage memoryStorage)
                    {
                        var sourceBuffer = memoryStorage.Stream.GetBuffer();
                        var sourceHandle = GCHandle.Alloc(sourceBuffer, GCHandleType.Pinned);
                        try
                        {
                            using var sourceDoc = Pdfium.PdfDocument.Load(sourceHandle.AddrOfPinnedObject(),
                                (int) memoryStorage.Stream.Length);
                            CopyPage(destDoc, sourceDoc, state);
                        }
                        finally
                        {
                            sourceHandle.Free();
                        }
                    }
                    if (state.OcrTask?.Result != null)
                    {
                        using var page = destDoc.GetPage(state.PageIndex);
                        DrawOcrTextOnPdfiumPage(state.Page, destDoc, page, state.OcrTask.Result);
                    }
                    if (progress.IsCancellationRequested) return false;
                }
                output.SaveDoc(destDoc);
                return true;
            }
            finally
            {
                destHandle.Free();
            }
        }
    }

    private void CopyPage(Pdfium.PdfDocument destDoc, Pdfium.PdfDocument sourceDoc, PageExportState state)
    {
        destDoc.ImportPages(sourceDoc, "1", state.PageIndex);
    }

    private static PdfDocument InitializeDocument(PdfExportParams exportParams)
    {
        var document = new PdfDocument();
        var creator = exportParams.Metadata.Creator;
        document.Info.Creator = string.IsNullOrEmpty(creator) ? "NAPS2" : creator;
        document.Info.Author = exportParams.Metadata.Author;
        document.Info.Keywords = exportParams.Metadata.Keywords;
        document.Info.Subject = exportParams.Metadata.Subject;
        document.Info.Title = exportParams.Metadata.Title;

        if (exportParams.Encryption?.EncryptPdf == true
            && (!string.IsNullOrEmpty(exportParams.Encryption.OwnerPassword) ||
                !string.IsNullOrEmpty(exportParams.Encryption.UserPassword)))
        {
            document.SecuritySettings.DocumentSecurityLevel = PdfDocumentSecurityLevel.Encrypted128Bit;
            if (!string.IsNullOrEmpty(exportParams.Encryption.OwnerPassword))
            {
                document.SecuritySettings.OwnerPassword = exportParams.Encryption.OwnerPassword;
            }

            if (!string.IsNullOrEmpty(exportParams.Encryption.UserPassword))
            {
                document.SecuritySettings.UserPassword = exportParams.Encryption.UserPassword;
            }

            document.SecuritySettings.PermitAccessibilityExtractContent =
                exportParams.Encryption.AllowContentCopyingForAccessibility;
            document.SecuritySettings.PermitAnnotations = exportParams.Encryption.AllowAnnotations;
            document.SecuritySettings.PermitAssembleDocument =
                exportParams.Encryption.AllowDocumentAssembly;
            document.SecuritySettings.PermitExtractContent = exportParams.Encryption.AllowContentCopying;
            document.SecuritySettings.PermitFormsFill = exportParams.Encryption.AllowFormFilling;
            document.SecuritySettings.PermitFullQualityPrint =
                exportParams.Encryption.AllowFullQualityPrinting;
            document.SecuritySettings.PermitModifyDocument =
                exportParams.Encryption.AllowDocumentModification;
            document.SecuritySettings.PermitPrint = exportParams.Encryption.AllowPrinting;
        }
        return document;
    }

    private PageExportState RenderStep(PageExportState state)
    {
        if (state.CancelToken.IsCancellationRequested) return state;
        state.Embedder = GetRenderedImageOrDirectJpegEmbedder(state);
        return state;
    }

    private IEmbedder GetRenderedImageOrDirectJpegEmbedder(PageExportState state)
    {
        if (state.Image.IsUntransformedJpegFile(out var jpegPath))
        {
            // Special case if we have an un-transformed JPEG - just use the original file instead of re-encoding
            using var fileStream = new FileStream(jpegPath, FileMode.Open, FileAccess.Read);
            var jpegHeader = JpegFormatHelper.ReadHeader(fileStream);
            // Ensure it's not a grayscale image as those are known to not be embeddable
            if (jpegHeader is { NumComponents: > 1 })
            {
                return new DirectJpegEmbedder(jpegHeader, jpegPath);
            }
        }
        return new RenderedImageEmbedder(state.Image.Render());
    }

    private PageExportState WriteToPdfSharpStep(PageExportState state)
    {
        if (state.CancelToken.IsCancellationRequested) return state;
        lock (state.Document)
        {
            var exportFormat = state.Embedder!.PrepareForExport(state.Image.Metadata);
            DrawImageOnPage(state.Page, state.Embedder, state.Image.Metadata.PageSize, exportFormat, state.Compat);
            if (state.OcrTask?.Result != null)
            {
                DrawOcrTextOnPage(state.Page, state.OcrTask.Result);
            }
        }
        state.IncrementProgress();
        return state;
    }

    private static MemoryStream FinalizeAndSaveDocument(PdfDocument document, PdfExportParams exportParams)
    {
        var compat = exportParams.Compat;
        var now = DateTime.Now;
        document.Info.CreationDate = now;
        document.Info.ModificationDate = now;
        if (compat == PdfCompat.PdfA1B)
        {
            PdfAHelper.SetCidStream(document);
            PdfAHelper.DisableTransparency(document);
        }

        if (compat != PdfCompat.Default)
        {
            PdfAHelper.SetColorProfile(document);
            PdfAHelper.SetCidMap(document);
            PdfAHelper.CreateXmpMetadata(document, compat);
        }

        var stream = new MemoryStream();
        document.Save(stream);
        return stream;
    }

    private IOcrEngine? GetOcrEngine(OcrParams? ocrParams)
    {
        if (ocrParams?.LanguageCode != null)
        {
            var activeEngine = _scanningContext.OcrEngine;
            if (activeEngine == null)
            {
                _logger.LogError("Supported OCR engine not installed.");
            }
            else
            {
                return activeEngine;
            }
        }
        return null;
    }

    private PageExportState InitOcrStep(PageExportState state)
    {
        if (state.CancelToken.IsCancellationRequested) return state;
        var ext = state.Embedder!.OriginalFileFormat == ImageFileFormat.Png ? ".png" : ".jpg";
        string ocrTempFilePath = Path.Combine(_scanningContext.TempFolderPath, Path.GetRandomFileName() + ext);
        if (!_scanningContext.OcrRequestQueue.HasCachedResult(state.OcrEngine!, state.Image, state.OcrParams!))
        {
            // Save the image to a file for use in OCR.
            // We don't need to delete this file as long as we pass it to OcrRequestQueue.Enqueue, which takes 
            // ownership and guarantees its eventual deletion.
            using var fileStream = new FileStream(ocrTempFilePath, FileMode.Create, FileAccess.Write);
            state.Embedder.CopyToStream(fileStream);
        }

        // Start OCR
        state.OcrTask = _scanningContext.OcrRequestQueue.Enqueue(
            _scanningContext, state.OcrEngine!, state.Image, ocrTempFilePath, state.OcrParams!, OcrPriority.Foreground,
            state.CancelToken);
        return state;
    }

    private async Task<PageExportState> WaitForOcrStep(PageExportState state)
    {
        if (state.CancelToken.IsCancellationRequested) return state;
        await state.OcrTask!;
        return state;
    }

    private PageExportState CheckIfOcrNeededStep(PageExportState state)
    {
        if (state.CancelToken.IsCancellationRequested) return state;
        try
        {
            if (state.Image.Storage is ImageFileStorage fileStorage)
            {
                state.PageDocument = PdfReader.Open(fileStorage.FullPath, PdfDocumentOpenMode.Import);
                state.NeedsOcr = !new PdfiumPdfReader()
                    .ReadTextByPage(fileStorage.FullPath)
                    .Any(x => x.Trim().Length > 0);
            }
            else if (state.Image.Storage is ImageMemoryStorage memoryStorage)
            {
                state.PageDocument = PdfReader.Open(memoryStorage.Stream, PdfDocumentOpenMode.Import);
                state.NeedsOcr = !new PdfiumPdfReader()
                    .ReadTextByPage(memoryStorage.Stream.GetBuffer(), (int) memoryStorage.Stream.Length)
                    .Any(x => x.Trim().Length > 0);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not import PDF page for possible OCR, falling back to non-OCR path");
        }
        if (!state.NeedsOcr)
        {
            // TODO: Could also switch around the checks, not sure which order is better
            state.PageDocument?.Close();
        }
        return state;
    }

    private static void DrawOcrTextOnPage(PdfPage page, OcrResult ocrResult)
    {
#if DEBUG && DEBUGOCR
            using XGraphics gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
#else
        using XGraphics gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Prepend);
#endif
        var tf = new XTextFormatter(gfx);
        foreach (var element in ocrResult.Elements)
        {
            var info = GetTextDrawInfo(page, gfx, ocrResult, element);
            if (info == null) continue;
#if DEBUG && DEBUGOCR
            gfx.DrawRectangle(new XPen(XColor.FromArgb(255, 0, 0)), info.Bounds);
#endif
            tf.DrawString(info.Text, info.Font, XBrushes.Transparent, info.Bounds);
        }
    }

    private static void DrawOcrTextOnPdfiumPage(PdfPage page, Pdfium.PdfDocument pdfiumDocument,
        Pdfium.PdfPage pdfiumPage, OcrResult ocrResult)
    {
        using XGraphics gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Prepend);
        foreach (var element in ocrResult.Elements)
        {
            var info = GetTextDrawInfo(page, gfx, ocrResult, element);
            if (info == null) continue;

            // TODO: We should embed the font data, both for PDF compatibility (e.g. PDF/A) and for Linux support 
            var textObj = pdfiumDocument.NewText("TimesNewRoman", info.FontSize);
            textObj.TextRenderMode = TextRenderMode.Invisible;
            textObj.SetText(info.Text);
            // This ends up being slightly different alignment then the PdfSharp-based text. Maybe at some point we can
            // try to make them identical, although it's not perfect to begin with.
            textObj.Matrix = new PdfMatrix(1, 0, 0, 1, info.X, (float) page.Height - (info.Y + info.TextHeight));
            pdfiumPage.InsertObject(textObj);
        }
        pdfiumPage.GenerateContent();
    }

    private static TextDrawInfo? GetTextDrawInfo(PdfPage page, XGraphics gfx, OcrResult ocrResult,
        OcrResultElement element)
    {
        if (string.IsNullOrEmpty(element.Text)) return null;

        var adjustedBounds = AdjustBounds(element.Bounds, (float) page.Width / ocrResult.PageBounds.w,
            (float) page.Height / ocrResult.PageBounds.h);
        var adjustedFontSize = CalculateFontSize(element.Text, adjustedBounds, gfx);
        // Special case to avoid accidentally recognizing big lines as dashes/underscores
        if (adjustedFontSize > 100 && (element.Text == "-" || element.Text == "_")) return null;
        var font = new XFont(GetFontName(), adjustedFontSize, XFontStyle.Regular,
            new XPdfFontOptions(PdfFontEncoding.Unicode));
        var adjustedTextSize = gfx.MeasureString(element.Text, font);
        var verticalOffset = (adjustedBounds.Height - adjustedTextSize.Height) / 2;
        var horizontalOffset = (adjustedBounds.Width - adjustedTextSize.Width) / 2;
        adjustedBounds.Offset((float) horizontalOffset, (float) verticalOffset);

        return new TextDrawInfo(
            element.RightToLeft ? ReverseText(element.Text) : element.Text,
            font,
            adjustedBounds,
            adjustedTextSize);
    }

    private static string ReverseText(string text)
    {
        TextElementEnumerator enumerator = StringInfo.GetTextElementEnumerator(text);
        var elements = new List<string>();
        while (enumerator.MoveNext())
        {
            elements.Add(enumerator.GetTextElement());
        }
        elements.Reverse();
        return string.Concat(elements);
    }

    private void DrawImageOnPage(PdfPage page, IEmbedder embedder, PageSize? pageSize, ImageExportFormat exportFormat,
        PdfCompat compat)
    {
        using var xImage = XImage.FromImageSource(new ImageSource(embedder, exportFormat));
        if (compat != PdfCompat.Default)
        {
            xImage.Interpolate = false;
        }
        var (realWidth, realHeight) = GetRealSize(embedder, pageSize);
        page.Width = realWidth;
        page.Height = realHeight;
        using XGraphics gfx = XGraphics.FromPdfPage(page);
        gfx.DrawImage(xImage, 0, 0, realWidth, realHeight);
    }

    private static (double width, double height) GetRealSize(IEmbedder embedder, PageSize? pageSize)
    {
        double hAdjust = 72 / embedder.HorizontalDpi;
        double vAdjust = 72 / embedder.VerticalDpi;
        if (double.IsInfinity(hAdjust) || double.IsInfinity(vAdjust))
        {
            hAdjust = vAdjust = 0.75;
        }
        double realWidth = embedder.Width * hAdjust;
        double realHeight = embedder.Height * vAdjust;

        // Use the scanned page size if it's close enough
        // It might not be close enough if we've cropped the image or if the scanner didn't produce the requested size
        if (pageSize != null)
        {
            var pageHorDpi = embedder.Width / (double) pageSize.WidthInInches;
            var pageVerDpi = embedder.Height / (double) pageSize.HeightInInches;
            // We expect a margin of error of <1 since most of the inaccuracy comes from file formats like JPEG only
            // storing integral DPIs
            if (Math.Abs(embedder.HorizontalDpi - pageHorDpi) <= 1 &&
                Math.Abs(embedder.VerticalDpi - pageVerDpi) <= 1)
            {
                realWidth = (double) pageSize.WidthInInches * 72;
                realHeight = (double) pageSize.HeightInInches * 72;
            }
        }

        // PDF page size precision is 3 decimal places and image matrix precision is 4 decimal places.
        // We round to 3 decimal places to ensure they match exactly.
        realWidth = Math.Round(realWidth, 3);
        realHeight = Math.Round(realHeight, 3);

        return (realWidth, realHeight);
    }

    private static XRect AdjustBounds((int x, int y, int w, int h) bounds, float hAdjust, float vAdjust) =>
        new XRect(bounds.x * hAdjust, bounds.y * vAdjust, bounds.w * hAdjust, bounds.h * vAdjust);

    private static int CalculateFontSize(string text, XRect adjustedBounds, XGraphics gfx)
    {
        int fontSizeGuess = Math.Max(1, (int) (adjustedBounds.Height));
        var measuredBoundsForGuess =
            gfx.MeasureString(text, new XFont(GetFontName(), fontSizeGuess, XFontStyle.Regular));
        double adjustmentFactor = adjustedBounds.Width / measuredBoundsForGuess.Width;
        int adjustedFontSize = Math.Max(1, (int) Math.Floor(fontSizeGuess * adjustmentFactor));
        return adjustedFontSize;
    }

    private static string GetFontName()
    {
#if NET6_0_OR_GREATER
        if (OperatingSystem.IsLinux())
        {
            // Liberation Serif is broadly included in Linux distros and is designed to have the same measurements
            // as Times New Roman.
            // TODO: Maybe we should use Times New Roman if available?
            return "Liberation Serif";
        }
#endif
        return "Times New Roman";
    }

    private static bool IsPdfStorage(IImageStorage storage) => storage switch
    {
        ImageFileStorage fileStorage => Path.GetExtension(fileStorage.FullPath).ToLowerInvariant() == ".pdf",
        ImageMemoryStorage memoryStorage => memoryStorage.TypeHint == ".pdf",
        _ => false
    };

    private record TextDrawInfo(string Text, XFont Font, XRect Bounds, XSize TextSize)
    {
        public int FontSize => (int) Font.Size;
        public float X => (float) Bounds.X;
        public float Y => (float) Bounds.Y;
        public float Width => (float) Bounds.Width;
        public float Height => (float) Bounds.Height;
        public float TextWidth => (float) TextSize.Width;
        public float TextHeight => (float) TextSize.Height;
    }

    private class PageExportState
    {
        public PageExportState(ProcessedImage image, int pageIndex, PdfDocument document, PdfPage page,
            IOcrEngine? ocrEngine, OcrParams? ocrParams, Action incrementProgress, CancellationToken cancelToken,
            PdfCompat compat)
        {
            Image = image;
            PageIndex = pageIndex;
            Document = document;
            Page = page;
            OcrEngine = ocrEngine;
            OcrParams = ocrParams;
            IncrementProgress = incrementProgress;
            CancelToken = cancelToken;
            Compat = compat;
        }

        public ProcessedImage Image { get; }
        public int PageIndex { get; }

        public PdfDocument Document { get; }
        public PdfPage Page { get; set; }
        public IOcrEngine? OcrEngine { get; }
        public OcrParams? OcrParams { get; }
        public Action IncrementProgress { get; }
        public CancellationToken CancelToken { get; }
        public PdfCompat Compat { get; }

        public bool NeedsOcr { get; set; }
        public IEmbedder? Embedder { get; set; }
        public Task<OcrResult?>? OcrTask { get; set; }
        public PdfDocument? PageDocument { get; set; }
    }

    private class ImageSource : IImageSource
    {
        private readonly IEmbedder _embedder;
        private readonly ImageExportFormat _exportFormat;

        public ImageSource(IEmbedder embedder, ImageExportFormat exportFormat)
        {
            _embedder = embedder;
            _exportFormat = exportFormat;
        }

        public void SaveAsJpeg(MemoryStream ms)
        {
            _embedder.CopyToStream(ms);
        }

        public void SaveAsPdfBitmap(MemoryStream ms)
        {
            var image = _embedder.Image;
            var subPixelType = _exportFormat.PixelFormat switch
            {
                ImagePixelFormat.ARGB32 => SubPixelType.Bgra,
                ImagePixelFormat.RGB24 or ImagePixelFormat.Gray8 => SubPixelType.Bgr,
                _ => throw new InvalidOperationException("Expected 8/24/32 bit bitmap")
            };
            var dstPixelInfo =
                new PixelInfo(image.Width, image.Height, subPixelType, strideAlign: 4) { InvertY = true };
            ms.SetLength(dstPixelInfo.Length);
            new CopyBitwiseImageOp().Perform(image, ms.GetBuffer(), dstPixelInfo);
        }

        public void SaveAsPdfIndexedBitmap(MemoryStream ms)
        {
            var image = _embedder.Image;
            image.UpdateLogicalPixelFormat();
            if (image.LogicalPixelFormat != ImagePixelFormat.BW1)
                throw new InvalidOperationException("Expected 1 bit bitmap");
            var dstPixelInfo =
                new PixelInfo(image.Width, image.Height, SubPixelType.Bit) { InvertY = true };
            ms.SetLength(dstPixelInfo.Length);
            new CopyBitwiseImageOp().Perform(image, ms.GetBuffer(), dstPixelInfo);
        }

        public int Width => _embedder.Width;
        public int Height => _embedder.Height;
        public string? Name => null;

        public XImageFormat ImageFormat
        {
            get
            {
                if (_exportFormat.FileFormat == ImageFileFormat.Jpeg)
                {
                    return XImageFormat.Jpeg;
                }
                if (_exportFormat.PixelFormat == ImagePixelFormat.BW1)
                {
                    return XImageFormat.Indexed;
                }
                if (_exportFormat.PixelFormat == ImagePixelFormat.ARGB32)
                {
                    return XImageFormat.Argb32;
                }
                // TODO: Ideally we should have Gray8 support in PdfSharp
                if (_exportFormat.PixelFormat is ImagePixelFormat.RGB24 or ImagePixelFormat.Gray8)
                {
                    return XImageFormat.Rgb24;
                }
                throw new Exception($"Unsupported pixel format: {_exportFormat.PixelFormat}");
            }
        }
    }

    private interface IEmbedder : IDisposable
    {
        void CopyToStream(Stream stream);
        ImageExportFormat PrepareForExport(ImageMetadata metadata);
        IMemoryImage Image { get; }
        int Width { get; }
        int Height { get; }
        double HorizontalDpi { get; }
        double VerticalDpi { get; }
        ImageFileFormat OriginalFileFormat { get; }
    }

    private class RenderedImageEmbedder : IEmbedder
    {
        public RenderedImageEmbedder(IMemoryImage image)
        {
            Image = image;
        }

        public IMemoryImage Image { get; private set; }
        public int Width => Image.Width;
        public int Height => Image.Height;
        public double HorizontalDpi => Image.HorizontalResolution;
        public double VerticalDpi => Image.VerticalResolution;
        public ImageFileFormat OriginalFileFormat => Image.OriginalFileFormat;

        public void CopyToStream(Stream stream)
        {
            // PDFs require RGB channels so we need to make sure we're exporting that.
            Image.Save(stream, ImageFileFormat.Jpeg, new ImageSaveOptions { PixelFormatHint = ImagePixelFormat.RGB24 });
        }

        public ImageExportFormat PrepareForExport(ImageMetadata metadata)
        {
            var exportFormat = new ImageExportHelper().GetExportFormat(Image, metadata.BitDepth, metadata.Lossless);
            if (exportFormat.FileFormat == ImageFileFormat.Unspecified)
            {
                exportFormat = exportFormat with { FileFormat = ImageFileFormat.Jpeg };
            }
            if (exportFormat.PixelFormat == ImagePixelFormat.BW1 &&
                Image.LogicalPixelFormat != ImagePixelFormat.BW1)
            {
                Image = Image.PerformTransform(new BlackWhiteTransform());
            }
            return exportFormat;
        }

        public void Dispose()
        {
            Image.Dispose();
        }
    }

    private class DirectJpegEmbedder : IEmbedder
    {
        private readonly JpegFormatHelper.JpegHeader _header;
        private readonly string _path;

        public DirectJpegEmbedder(JpegFormatHelper.JpegHeader header, string path)
        {
            _header = header;
            _path = path;
        }

        public IMemoryImage Image => throw new InvalidOperationException();
        public int Width => _header.Width;
        public int Height => _header.Height;
        public double HorizontalDpi => _header.HorizontalDpi;
        public double VerticalDpi => _header.VerticalDpi;
        public ImageFileFormat OriginalFileFormat => ImageFileFormat.Jpeg;

        public void CopyToStream(Stream stream)
        {
            using var fileStream = new FileStream(_path, FileMode.Open, FileAccess.Read);
            fileStream.CopyTo(stream);
        }

        public ImageExportFormat PrepareForExport(ImageMetadata metadata)
        {
            return new ImageExportFormat(ImageFileFormat.Jpeg, ImagePixelFormat.RGB24);
        }

        public void Dispose()
        {
        }
    }

    private record OutputPathOrStream(string? Path, Stream? Stream)
    {
        public void CopyFromStream(MemoryStream inputStream)
        {
            if (Stream != null)
            {
                inputStream.CopyTo(Stream);
            }
            else
            {
                FileSystemHelper.EnsureParentDirExists(Path!);
                using var fileStream = new FileStream(Path!, FileMode.Create);
                inputStream.CopyTo(fileStream);
            }
        }

        public void SaveDoc(Pdfium.PdfDocument doc)
        {
            if (Stream != null)
            {
                doc.Save(Stream);
            }
            else
            {
                FileSystemHelper.EnsureParentDirExists(Path!);
                doc.Save(Path!);
            }
        }
    }
}