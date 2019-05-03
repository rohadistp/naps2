﻿using System;
using System.Collections.Generic;
using System.Linq;

namespace NAPS2.WinForms
{
    public class StubDialogHelper : DialogHelper
    {
        public override bool PromptToSavePdfOrImage(string defaultPath, out string savePath)
        {
            savePath = null;
            return false;
        }

        public override bool PromptToSavePdf(string defaultPath, out string savePath)
        {
            savePath = null;
            return false;
        }

        public override bool PromptToSaveImage(string defaultPath, out string savePath)
        {
            savePath = null;
            return false;
        }
    }
}