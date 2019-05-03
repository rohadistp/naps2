﻿using System;
using System.Collections.Generic;
using System.Linq;

namespace NAPS2.Util
{
    public class StubErrorOutput : ErrorOutput
    {
        public override void DisplayError(string errorMessage)
        {
        }

        public override void DisplayError(string errorMessage, string details)
        {
        }

        public override void DisplayError(string errorMessage, Exception exception)
        {
        }
    }
}