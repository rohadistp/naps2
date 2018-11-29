﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using NAPS2.Images.Storage;
using NAPS2.Scan;

namespace NAPS2.Images
{
    public class ThresholdBlankDetector : IBlankDetector
    {
        // If the pixel value (0-255) >= white_threshold, then it counts as a white pixel.
        private const int WHITE_THRESHOLD_MIN = 1;
        private const int WHITE_THRESHOLD_MAX = 255;
        // If the fraction of non-white pixels > coverage_threshold, then it counts as a non-blank page.
        private const double COVERAGE_THRESHOLD_MIN = 0.00;
        private const double COVERAGE_THRESHOLD_MAX = 0.01;

        public bool IsBlank(IImage image, int whiteThresholdNorm, int coverageThresholdNorm)
        {
            if (image.PixelFormat == StoragePixelFormat.BW1)
            {
                // TODO: Make more generic
                if (!(image is GdiImage gdiImage))
                {
                    throw new InvalidOperationException("Patch code detection only supported for GdiStorage");
                }
                using (var bitmap2 = BitmapHelper.CopyToBpp(gdiImage.Bitmap, 8))
                {
                    return IsBlankRGB(new GdiImage(bitmap2), whiteThresholdNorm, coverageThresholdNorm);
                }
            }
            if (image.PixelFormat != StoragePixelFormat.RGB24)
            {
                return false;
            }
            return IsBlankRGB(image, whiteThresholdNorm, coverageThresholdNorm);
        }

        private static bool IsBlankRGB(IImage image, int whiteThresholdNorm, int coverageThresholdNorm)
        {
            var whiteThreshold = (int)Math.Round(WHITE_THRESHOLD_MIN + (whiteThresholdNorm / 100.0) * (WHITE_THRESHOLD_MAX - WHITE_THRESHOLD_MIN));
            var coverageThreshold = COVERAGE_THRESHOLD_MIN + (coverageThresholdNorm / 100.0) * (COVERAGE_THRESHOLD_MAX - COVERAGE_THRESHOLD_MIN);

            long totalPixels = image.Width * image.Height;
            long matchPixels = 0;

            var data = image.Lock(out var scan0, out var stride);
            var bytes = new byte[stride * image.Height];
            Marshal.Copy(scan0, bytes, 0, bytes.Length);
            image.Unlock(data);
            for (int x = 0; x < image.Width; x++)
            {
                for (int y = 0; y < image.Height; y++)
                {
                    int r = bytes[stride * y + x * 3];
                    int g = bytes[stride * y + x * 3 + 1];
                    int b = bytes[stride * y + x * 3 + 2];
                    // Use standard values for grayscale conversion to weight the RGB values
                    int luma = r * 299 + g * 587 + b * 114;
                    if (luma < whiteThreshold * 1000)
                    {
                        matchPixels++;
                    }
                }
            }

            var coverage = (matchPixels / (double)totalPixels);
            return coverage < coverageThreshold;
        }

        public bool ExcludePage(IImage image, ScanProfile scanProfile)
        {
            return scanProfile.ExcludeBlankPages && IsBlank(image, scanProfile.BlankPageWhiteThreshold, scanProfile.BlankPageCoverageThreshold);
        }
    }
}