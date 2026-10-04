using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace KASTN;

// Share the immutable path data across rows; only the two displayed icons need
// controls. Brushes stay dynamic resources so theme changes still apply.
internal sealed class KastnTreeIconConverter : IValueConverter
{
    private static readonly IReadOnlyDictionary<KastnTreeIconKind, Geometry> geometries = new Dictionary<KastnTreeIconKind, Geometry>
    {
        [KastnTreeIconKind.Bucket] = Geometry.Parse("M2 5h7l2 2h11v12H2z"),
        [KastnTreeIconKind.Container] = Geometry.Parse("M3 4h18v16H3z M3 9h18"),
        [KastnTreeIconKind.Text] = Geometry.Parse("M5 2h10l4 4v16H5z M15 2v5h4 M8 11h8 M8 15h8"),
        [KastnTreeIconKind.Picture] = Geometry.Parse("M3 4h18v16H3z M6 16l4-4 3 3 2-2 3 3 M16 8h.01"),
        [KastnTreeIconKind.Structural] = Geometry.Parse("M3 12h18"),
        [KastnTreeIconKind.OpenEye] = Geometry.Parse("M2 12s3.5-6 10-6 10 6 10 6-3.5 6-10 6S2 12 2 12z M9 12a3 3 0 1 0 6 0 3 3 0 1 0-6 0"),
        [KastnTreeIconKind.ClosedEye] = Geometry.Parse("M9.88 9.88a3 3 0 1 0 4.24 4.24M10.73 5.08A10.43 10.43 0 0 1 12 5c7 0 10 7 10 7a13.16 13.16 0 0 1-1.67 2.68M6.61 6.61A13.526 13.526 0 0 0 2 12s3 7 10 7a9.74 9.74 0 0 0 5.39-1.61M2 2l20 20")
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is KastnTreeIconKind kind ? geometries.GetValueOrDefault(kind) : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
