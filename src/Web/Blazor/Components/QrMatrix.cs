using System.Text;
using QRCoder;

namespace NexaVerify.Web.Components;

/// <summary>Turns text into QR modules and then into one SVG path (every dark module is a 1x1 square).</summary>
internal static class QrMatrix
{
    public static (int Size, string Path) Draw(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return (0, string.Empty);
        }

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(value, QRCodeGenerator.ECCLevel.M);
        var rows = data.ModuleMatrix; // includes the quiet zone scanners need
        var size = rows.Count;
        var path = new StringBuilder(size * size);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                if (rows[y][x])
                {
                    path.Append('M').Append(x).Append(',').Append(y).Append("h1v1h-1z");
                }
            }
        }

        return (size, path.ToString());
    }
}
