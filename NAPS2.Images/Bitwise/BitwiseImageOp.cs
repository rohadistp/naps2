namespace NAPS2.Images.Bitwise;

public class BitwiseImageOp
{
    protected unsafe void ValidateConsistency(BitwiseImageData data)
    {
        if (data.ptr == (byte*)IntPtr.Zero)
        {
            throw new ArgumentException("Null data");
        }
        if (data.bytesPerPixel > 0 && data.bitsPerPixel != data.bytesPerPixel * 8)
        {
            throw new ArgumentException("Invalid bits per pixel");
        }
        if (!(data.bytesPerPixel is 1 or 3 or 4 || data.bytesPerPixel == 0 && data.bitsPerPixel == 1))
        {
            throw new ArgumentException("Invalid bytes per pixel");
        }
        if (data.bytesPerPixel > 0 && data.stride < data.w * data.bytesPerPixel)
        {
            throw new ArgumentException("Invalid stride");
        }
        if (data.bytesPerPixel == 4)
        {
            var offsets = new[] { data.rOff, data.gOff, data.bOff, data.aOff };
            if (offsets.Distinct().Count() < 4 || offsets.Any(x => x is < 0 or > 3))
            {
                throw new ArgumentException("Invalid offsets");
            }
        }
        if (data.bytesPerPixel == 3)
        {
            var offsets = new[] { data.rOff, data.gOff, data.bOff };
            if (offsets.Distinct().Count() < 3 || offsets.Any(x => x is < 0 or > 2))
            {
                throw new ArgumentException("Invalid offsets");
            }
        }
    }
}