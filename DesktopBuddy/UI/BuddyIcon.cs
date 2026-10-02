using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace DesktopBuddy.UI;

public enum BuddyMood { Calm, Warm, Hot, Paused }

/// <summary>Draws the tray icon: a coloured face whose colour follows the hottest temperature.</summary>
internal static class BuddyIcon
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Icon Create(BuddyMood mood)
    {
        Color fill = mood switch
        {
            BuddyMood.Warm => Color.FromArgb(245, 158, 11),
            BuddyMood.Hot => Color.FromArgb(220, 38, 38),
            BuddyMood.Paused => Color.FromArgb(120, 120, 120),
            _ => Color.FromArgb(22, 163, 74),
        };

        using var bitmap = new Bitmap(32, 32);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var body = new SolidBrush(fill);
            g.FillEllipse(body, 1, 1, 30, 30);
            using var white = new SolidBrush(Color.White);
            g.FillEllipse(white, 9, 10, 5, 6);
            g.FillEllipse(white, 18, 10, 5, 6);
            using var pen = new Pen(Color.White, 2.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            if (mood == BuddyMood.Hot) g.DrawArc(pen, 10, 20, 12, 7, 200, 140);  // frown
            else g.DrawArc(pen, 10, 15, 12, 8, 20, 140);                           // smile
        }

        IntPtr handle = bitmap.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }
}
