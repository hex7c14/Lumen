using Lumen.Core.Imaging;

namespace Lumen.Core.Tests;

public sealed class ImagePixelsTests
{
    [Fact]
    public void FromBgra_ConvertsChannelOrder()
    {
        byte[] bgra = [10, 20, 30, 40, 50, 60, 70, 80];

        ImagePixels pixels = ImagePixels.FromBgra(bgra, 2, 1);

        Assert.Equal(2, pixels.Width);
        Assert.Equal(1, pixels.Height);
        Assert.Equal(new byte[] { 30, 20, 10, 40, 70, 60, 50, 80 }, pixels.Pixels);
    }

    [Fact]
    public void FromBgra_TooShort_Throws()
    {
        Assert.Throws<ArgumentException>(() => ImagePixels.FromBgra(new byte[7], 2, 1));
    }

    [Fact]
    public void FromBgra_IgnoresExtraBytes()
    {
        ImagePixels pixels = ImagePixels.FromBgra(new byte[12], 1, 1);
        Assert.Equal(4, pixels.Pixels.Length);
    }

    [Fact]
    public void CopyToBgraPremultiplied_OpaqueJustSwapsChannels()
    {
        var source = new ImagePixels(1, 1);
        source.Pixels[0] = 10;
        source.Pixels[1] = 20;
        source.Pixels[2] = 30;
        source.Pixels[3] = 255;
        var destination = new byte[4];

        source.CopyToBgraPremultiplied(destination);

        Assert.Equal(new byte[] { 30, 20, 10, 255 }, destination);
    }

    [Fact]
    public void CopyToBgraPremultiplied_PremultipliesWithRounding()
    {
        var source = new ImagePixels(1, 1);
        source.Pixels[0] = 255; // R
        source.Pixels[1] = 100; // G
        source.Pixels[2] = 50;  // B
        source.Pixels[3] = 128; // A
        var destination = new byte[4];

        source.CopyToBgraPremultiplied(destination);

        Assert.Equal((byte)25, destination[0]);  // B: (50*128+127)/255 = 25.5 -> 25
        Assert.Equal((byte)50, destination[1]);  // G: (100*128+127)/255 = 50.7 -> 50
        Assert.Equal((byte)128, destination[2]); // R: (255*128+127)/255 = 128.49 -> 128
        Assert.Equal((byte)128, destination[3]);
    }

    [Fact]
    public void CopyToBgraPremultiplied_FullyTransparent_IsAllZero()
    {
        var source = new ImagePixels(1, 1);
        source.Pixels[0] = 200;
        source.Pixels[1] = 150;
        source.Pixels[2] = 100;
        source.Pixels[3] = 0;
        var destination = new byte[] { 9, 9, 9, 9 };

        source.CopyToBgraPremultiplied(destination);

        Assert.Equal(new byte[] { 0, 0, 0, 0 }, destination);
    }

    [Fact]
    public void CopyToBgraPremultiplied_TooShort_Throws()
    {
        var source = TestImages.CreateGradient(2, 2);
        Assert.Throws<ArgumentException>(() => source.CopyToBgraPremultiplied(new byte[15]));
    }

    [Fact]
    public void Constructor_ValidatesArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImagePixels(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImagePixels(1, 0));
        Assert.Throws<ArgumentNullException>(() => new ImagePixels(1, 1, null!));
        Assert.Throws<ArgumentException>(() => new ImagePixels(2, 2, new byte[15]));
    }

    [Fact]
    public void HasTransparency_DetectsNonOpaquePixels()
    {
        var opaque = new ImagePixels(2, 2);
        for (int i = 3; i < opaque.Pixels.Length; i += ImagePixels.BytesPerPixel)
        {
            opaque.Pixels[i] = 255;
        }

        Assert.False(opaque.HasTransparency());

        opaque.Pixels[7] = 254;
        Assert.True(opaque.HasTransparency());
    }

    [Fact]
    public void Clone_CopiesPixels()
    {
        ImagePixels source = TestImages.CreateGradient(4, 4);
        ImagePixels clone = source.Clone();

        Assert.NotSame(source, clone);
        Assert.Equal(source.Pixels, clone.Pixels);

        clone.Pixels[0] ^= 0xFF;
        Assert.NotEqual(source.Pixels[0], clone.Pixels[0]);
    }
}
