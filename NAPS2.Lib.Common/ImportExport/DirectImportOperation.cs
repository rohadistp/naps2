﻿using NAPS2.ImportExport.Images;
using NAPS2.Scan;
using NAPS2.Serialization;

namespace NAPS2.ImportExport;

public class DirectImportOperation : OperationBase
{
    private readonly ScanningContext _scanningContext;
    private readonly ImportPostProcessor _importPostProcessor;

    public DirectImportOperation(ScanningContext scanningContext, ImportPostProcessor importPostProcessor)
    {
        _scanningContext = scanningContext;
        _importPostProcessor = importPostProcessor;

        AllowCancel = true;
        AllowBackground = true;
    }

    public bool Start(ImageTransferData data, bool copy, Action<ProcessedImage> imageCallback, DirectImportParams importParams)
    {
        ProgressTitle = copy ? MiscResources.CopyProgress : MiscResources.ImportProgress;
        Status = new OperationStatus
        {
            StatusText = copy ? MiscResources.Copying : MiscResources.Importing,
            MaxProgress = data.SerializedImages.Count
        };

        RunAsync(() =>
        {
            Exception? error = null;
            foreach (var serializedImage in data.SerializedImages)
            {
                try
                {
                    ProcessedImage img = SerializedImageHelper.Deserialize(_scanningContext, serializedImage, new SerializedImageHelper.DeserializeOptions());
                    var thumbnailSize = img.PostProcessingData.Thumbnail == null ? importParams.ThumbnailSize : null;
                    // TODO: Maybe we can use a Pipeline to offload thumbnail rendering?
                    // TODO: And actually we should be using the worker service here...
                    // TODO: Or maybe somehow we can just leverage the FDesktop renderer
                    // TODO: And also we should try and refactor as much logic out of FDesktop etc to make it testable
                    // TODO: Also consider if multiple workers (e.g. 4 in parallel) here and elsewhere is a good idea to optimize rendering
                    img = _importPostProcessor.AddPostProcessingData(
                        img,
                        null,
                        thumbnailSize,
                        new BarcodeDetectionOptions(),
                        true);
                    imageCallback(img);

                    Status.CurrentProgress++;
                    InvokeStatusChanged();
                    if (CancelToken.IsCancellationRequested)
                    {
                        break;
                    }
                }
                catch (Exception ex)
                {
                    error = ex;
                }
            }
            if (error != null)
            {
                Log.ErrorException(string.Format(MiscResources.ImportErrorCouldNot, "<data>"), error);
            }
            return true;
        });
        return true;
    }
}