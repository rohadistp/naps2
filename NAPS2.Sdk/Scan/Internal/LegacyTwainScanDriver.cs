﻿using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NAPS2.Images.Storage;

namespace NAPS2.Scan.Internal
{
    internal class LegacyTwainScanDriver : IScanDriver
    {
        public Task<List<ScanDevice>> GetDeviceList(ScanOptions options) => throw new NotImplementedException();

        public Task Scan(ScanOptions options, CancellationToken cancelToken, IScanEvents scanEvents, Action<IImage> callback)
        {
            throw new NotImplementedException();
        }
    }
}