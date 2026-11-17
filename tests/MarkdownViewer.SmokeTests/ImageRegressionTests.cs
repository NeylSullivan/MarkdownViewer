using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static partial class Program
{
    private static async Task TestImageNaturalSize()
    {
        foreach (var (width, height, dpi) in new[] { (16, 16, 96), (16, 16, 192), (3200, 800, 96), (800, 3200, 96), (3200, 800, 192) })
        {
            var name = $"image-{width}-{height}-{dpi}.png";
            var bytes = ImagePng(width, height, dpi);
            await File.WriteAllBytesAsync(Fixture(name), bytes);
            var rendered = Render($"![Local image]({name})");
            rendered.Document.PageWidth = 1000;
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            VerifyImageDimensions(ImageHolder(rendered.Document), width, height, dpi);
            rendered.Document.PageWidth = 400;
            var reader = new RichTextBox { Document = rendered.Document, Width = 400, Height = 600, IsReadOnly = true };
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            reader.Measure(new Size(400, 600));
            reader.Arrange(new Rect(0, 0, 400, 600));
            reader.UpdateLayout();
            var holder = ImageHolder(rendered.Document);
            var image = (Image)holder.Child;
            Assert(image.ActualWidth <= holder.MaxWidth + .1, "Image fits the actual narrow document layout");
            Assert(image.ActualHeight <= 900.1, "Portrait image fits the display height limit");
        }

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            foreach (var (width, height) in new[] { (16, 16), (3200, 800) })
            {
                await Task.WhenAll(
                    ServeImageAsync(listener, ImagePng(width, height, 96), timeout.Token),
                    VerifyRemoteImageAsync(port, width, height, timeout.Token));
            }
        }
        finally { listener.Stop(); }
    }

    private static async Task VerifyRemoteImageAsync(int port, int width, int height, CancellationToken token)
    {
        var rendered = Render($"![Remote image](http://127.0.0.1:{port}/image-{width}.png)");
        rendered.Document.PageWidth = 1000;
        var holder = ImageHolder(rendered.Document);
        while (holder.Child is not Image)
            await Task.Delay(10, token);
        await Dispatcher.Yield(DispatcherPriority.DataBind);
        VerifyImageDimensions(holder, width, height, 96);
    }

    private static byte[] ImagePng(int width, int height, int dpi)
    {
        var bitmap = BitmapSource.Create(width, height, dpi, dpi, PixelFormats.Bgra32, null,
            new byte[width * height * 4], width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static Border ImageHolder(FlowDocument document) =>
        (Border)((Paragraph)document.Blocks.FirstBlock).Inlines.OfType<InlineUIContainer>().Single().Child;

    private static void VerifyImageDimensions(Border holder, int width, int height, int dpi)
    {
        Assert(holder.Child is Image, "Image decodes successfully");
        var image = (Image)holder.Child;
        var bitmap = (BitmapSource)image.Source;
        var decodeScale = Math.Min(1d, 1600d / Math.Max(width, height));
        Equal((int)(width * decodeScale), bitmap.PixelWidth, "Decoded width respects original size and pixel limit");
        Equal((int)(height * decodeScale), bitmap.PixelHeight, "Decoded height respects original size and pixel limit");
        holder.Measure(new Size(1000, 2000));
        holder.Arrange(new Rect(holder.DesiredSize));
        var naturalWidth = width * 96d / dpi;
        var naturalHeight = height * 96d / dpi;
        var displayScale = Math.Min(1d, 900d / Math.Max(naturalWidth, naturalHeight));
        Assert(Math.Abs(image.ActualWidth - naturalWidth * displayScale) < .1, "Image preserves its natural DIP width within the display limit");
        Assert(Math.Abs(image.ActualHeight - naturalHeight * displayScale) < .1, "Image preserves its natural DIP height within the display limit");
    }

    private static async Task ServeImageAsync(TcpListener listener, byte[] image, CancellationToken token)
    {
        using var client = await listener.AcceptTcpClientAsync(token);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(token))) { }
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: {image.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(image, token);
    }
}
