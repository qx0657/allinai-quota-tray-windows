using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace AllinAIQuotaTray;

internal static class ProgressIcon
{
    public static Icon Make(SummaryContent content)
    {
        if (content.Symbol == StatusSymbol.Quota)
            return Make(content.Progress, content.Progress <= 0.1 ? Color.Firebrick : content.Progress <= 0.3 ? Color.DarkOrange : Color.RoyalBlue);
        var glyph = content.Symbol switch { StatusSymbol.Balance => "$", StatusSymbol.Warning => "!", StatusSymbol.Account => "@", _ => "?" };
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var dark = new SolidBrush(Color.FromArgb(240, 0, 0, 0));
        using var light = new Pen(Color.White, 2.5f);
        graphics.FillEllipse(dark, 2, 2, 28, 28);
        graphics.DrawEllipse(light, 2, 2, 28, 28);
        using var font = new Font("Segoe UI", 21, FontStyle.Bold, GraphicsUnit.Pixel);
        using var text = new SolidBrush(content.Symbol == StatusSymbol.Warning ? Color.FromArgb(255, 160, 130) : Color.White);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.DrawString(glyph, font, text, new RectangleF(0, 0, 32, 30), format);
        var handle = bitmap.GetHicon();
        try { using var icon = Icon.FromHandle(handle); return (Icon)icon.Clone(); }
        finally { DestroyIcon(handle); }
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static Icon Make(double? progress, Color color)
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.Clear(Color.Transparent);

        var rect = new RectangleF(3.5f, 3.5f, 25, 25);
        using var darkOutline = new Pen(Color.FromArgb(220, 0, 0, 0), 5.8f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        using var lightTrack = new Pen(Color.FromArgb(245, 255, 255, 255), 4.6f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };

        graphics.DrawEllipse(darkOutline, rect);
        graphics.DrawEllipse(lightTrack, rect);

        if (progress.HasValue)
        {
            using var pen = new Pen(color, 3.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            graphics.DrawArc(pen, rect, -90, (float)(Math.Clamp(progress.Value, 0, 1) * 360));
        }
        else
        {
            using var outerBrush = new SolidBrush(Color.FromArgb(235, 0, 0, 0));
            using var innerBrush = new SolidBrush(color);
            using var innerBorder = new Pen(Color.FromArgb(250, 255, 255, 255), 2.4f);
            graphics.FillEllipse(outerBrush, 5.5f, 5.5f, 21, 21);
            graphics.FillEllipse(innerBrush, 8.0f, 8.0f, 16, 16);
            graphics.DrawEllipse(innerBorder, 8.0f, 8.0f, 16, 16);
        }

        if (progress.HasValue)
        {
            using var brush = new SolidBrush(color);
            using var centerBorder = new Pen(Color.FromArgb(240, 0, 0, 0), 1.5f);
            graphics.FillEllipse(brush, 12.2f, 12.2f, 7.6f, 7.6f);
            graphics.DrawEllipse(centerBorder, 12.2f, 12.2f, 7.6f, 7.6f);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }
}

internal static class StartupManager
{
    private const string AppName = "AllinAIQuotaTray";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var value = key?.GetValue(AppName) as string;
        return string.Equals(Unquote(value), Application.ExecutablePath, StringComparison.OrdinalIgnoreCase);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                       ?? throw new InvalidOperationException("无法打开当前用户启动项注册表。");
        if (enabled)
        {
            key.SetValue(AppName, Quote(Application.ExecutablePath), RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(AppName, throwOnMissingValue: false);
        }
    }

    private static string Quote(string path)
    {
        return $"\"{path}\"";
    }

    private static string? Unquote(string? value)
    {
        return value?.Trim().Trim('"');
    }
}

internal static class AppInfo
{
    public static string Version { get; } =
        typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?.Split('+')[0]
        ?? Application.ProductVersion.Split('+')[0];
}

internal static class CredentialVault
{
    private const string Service = "local.allinai.quota.tray.windows";
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite([In] ref NativeCredential credential, [In] uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, int flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree([In] IntPtr buffer);

    public static void Set(string account, string password)
    {
        var bytes = Encoding.Unicode.GetBytes(password);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredential
            {
                Type = CredTypeGeneric,
                TargetName = Target(account),
                CredentialBlob = blob,
                CredentialBlobSize = (uint)bytes.Length,
                Persist = CredPersistLocalMachine,
                UserName = account
            };

            if (!CredWrite(ref credential, 0))
            {
                throw new InvalidOperationException($"保存密码到 Windows 凭据管理器失败：{Marshal.GetLastWin32Error()}");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }
    }

    public static string? Get(string account)
    {
        if (!CredRead(Target(account), CredTypeGeneric, 0, out var credentialPtr))
        {
            if (Marshal.GetLastWin32Error() == ErrorNotFound) return null;
            throw new InvalidOperationException("无法读取 Windows 凭据管理器。");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPtr);
            if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero)
            {
                return string.Empty;
            }

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
        }
        finally
        {
            CredFree(credentialPtr);
        }
    }

    public static bool Contains(string account)
    {
        if (!CredRead(Target(account), CredTypeGeneric, 0, out var pointer))
        {
            if (Marshal.GetLastWin32Error() == ErrorNotFound) return false;
            throw new InvalidOperationException("无法访问 Windows 凭据管理器。");
        }
        CredFree(pointer);
        return true;
    }

    public static void Delete(string account)
    {
        if (CredDelete(Target(account), CredTypeGeneric, 0))
        {
            return;
        }

        var error = Marshal.GetLastWin32Error();
        if (error != ErrorNotFound)
        {
            throw new InvalidOperationException($"从 Windows 凭据管理器移除密码失败：{error}");
        }
    }

    private static string Target(string account)
    {
        return $"{Service}:{account}";
    }
}

internal sealed class WindowsCredentialStore : ICredentialStore
{
    public string? Get(string account) => CredentialVault.Get(account);
    public void Set(string account, string password) => CredentialVault.Set(account, password);
    public void Delete(string account) => CredentialVault.Delete(account);
    public bool Contains(string account) => CredentialVault.Contains(account);
}
