using System;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wpf.Ui.Appearance;

namespace WinResizer.Services;

internal static class ApplicationIconProvider
{
    private const string DarkIconResource = "WinResizer.Resources.AppIcon-light.ico";
    private const string LightIconResource = "WinResizer.Resources.AppIcon-dark.ico";
    private static readonly ImageSource DarkApplicationIcon = LoadIcon(DarkIconResource);
    private static readonly ImageSource LightApplicationIcon = LoadIcon(LightIconResource);

    internal static ImageSource ForTheme(ApplicationTheme theme) =>
        theme == ApplicationTheme.Dark ? LightApplicationIcon : DarkApplicationIcon;

    internal static ImageSource ForSystemMode(int? systemUsesLightTheme) =>
        SystemThemeIconProvider.SelectIconVariant(systemUsesLightTheme) == SystemThemeIconVariant.LightSymbol
            ? LightApplicationIcon
            : DarkApplicationIcon;

    private static ImageSource LoadIcon(string resourceName)
    {
        using (var stream = typeof(ApplicationIconProvider).Assembly.GetManifestResourceStream(resourceName))
        {
            if (stream is null)
            {
                throw new InvalidOperationException($"Application icon resource was not found: {resourceName}");
            }

            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            var frame = decoder.Frames
                .OrderBy(candidate => Math.Abs(candidate.PixelWidth - 32))
                .ThenBy(candidate => Math.Abs(candidate.PixelHeight - 32))
                .First();
            frame.Freeze();
            return frame;
        }
    }
}
