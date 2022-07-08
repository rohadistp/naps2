﻿using System.Threading;

namespace NAPS2.Scan;

public class ScanOperation : OperationBase
{
    private int _pageNumber = 1;

    public ScanOperation(ScanOptions options)
    {
        ProgressTitle = options.Device!.Name;
        Status = new OperationStatus
        {
            StatusText = options.PaperSource == PaperSource.Flatbed
                ? MiscResources.AcquiringData
                : string.Format(MiscResources.ScanProgressPage, _pageNumber),
            MaxProgress = 1000,
            ProgressType = OperationProgressType.BarOnly,
            IndeterminateProgress = options.Driver == Driver.Twain && options.TwainOptions.TransferMode != TwainTransferMode.Memory
        };
        AllowBackground = true;
        AllowCancel = true;
    }

    public new CancellationToken CancelToken => base.CancelToken;

    public void Progress(int current, int total)
    {
        Status.CurrentProgress = current;
        Status.MaxProgress = total;
        InvokeStatusChanged();
    }

    public void NextPage(int newPageNumber)
    {
        _pageNumber = newPageNumber;
        Status.StatusText = string.Format(MiscResources.ScanProgressPage, _pageNumber);
        InvokeStatusChanged();
    }

    public void Completed()
    {
        InvokeFinished();
    }
}