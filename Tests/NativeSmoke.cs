using System.Drawing.Imaging;
using AllinAIQuotaTray;

internal static class NativeSmoke
{
    [STAThread]
    public static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var directory = Path.GetFullPath(args.FirstOrDefault() ?? "publish/previews");
        Directory.CreateDirectory(directory);
        var states = new Dictionary<string, SummaryContent>
        {
            ["subscription"] = new() { Account = "fixture@example.invalid", Title = "订阅额度", Caption = "每周剩余额度", Amount = "$10.00",
                Percentage = "10%", Progress = 0.1, Symbol = StatusSymbol.Quota,
                Lines = ["已用 $90.00 / $100.00", "每日剩余 80% · $11.20", "还有 20 天 3 小时到期", "余额 $49.99", "刚刚更新"] },
            ["balance"] = new() { Account = "fixture@example.invalid", Title = "余额消费", Caption = "账户余额（USD）", Amount = "$49.99",
                Symbol = StatusSymbol.Balance, Lines = ["无有效订阅，可使用账户余额", "刚刚更新"] },
            ["exhausted"] = new() { Title = "订阅额度", Caption = "每日剩余额度", Amount = "$0.00", Percentage = "0%", Progress = 0,
                IsWarning = true, Symbol = StatusSymbol.Warning, Banner = "此订阅额度已用尽", Lines = ["已用 $14.00 / $14.00", "余额 $49.99", "刚刚更新"] },
            ["offline"] = new() { Title = "余额消费", Caption = "最近已知余额（USD）", Amount = "$49.99", Symbol = StatusSymbol.Unknown,
                Banner = "刷新失败 · 显示最近数据", Lines = ["无有效订阅，可使用账户余额", "5 分钟前更新", "无法连接服务，请检查网络后重试。"] },
            ["unknown"] = new() { Title = "账户状态", Caption = "暂时无法确认可用额度", Amount = "--", Symbol = StatusSymbol.Unknown,
                Lines = ["账户余额 $49.99", "订阅信息尚未成功获取。"] },
            ["signed-out"] = new() { Title = "欢迎使用 AllinAI", Caption = "请登录后查看额度与余额", Amount = "尚未登录",
                Symbol = StatusSymbol.Account, Lines = ["密码保存在 Windows 凭据管理器。"] }
        };
        foreach (var state in states)
        {
            using var form = new Form { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = Padding.Empty,
                ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000) };
            using var panel = new SummaryPanel(state.Value);
            form.Controls.Add(panel);
            // CreateControl on an invisible form leaves child HWNDs uncreated and produces an empty DrawToBitmap.
            form.Show(); Application.DoEvents(); form.PerformLayout(); panel.PerformLayout();
            if (panel.Width < 300 || panel.Height < 100) throw new Exception("Summary layout did not produce an account overview");
            using var bitmap = new Bitmap(panel.Width, panel.Height);
            panel.DrawToBitmap(bitmap, panel.ClientRectangle);
            AssertNotBlank(bitmap);
            bitmap.Save(Path.Combine(directory, state.Key + ".png"), ImageFormat.Png);
            using var icon = ProgressIcon.Make(state.Value);
            if (icon.Width == 0) throw new Exception("Missing tray icon");
            Console.WriteLine("PASS native summary and icon: " + state.Key);
            form.Close();
        }
        using (var login = new LoginForm("fixture@example.invalid", (_, _, _) => Task.CompletedTask))
        {
            login.StartPosition = FormStartPosition.Manual;
            login.Location = new Point(-20000, -20000);
            login.Show(); Application.DoEvents(); login.PerformLayout();
            using var bitmap = new Bitmap(login.ClientSize.Width, login.ClientSize.Height);
            login.Controls[0].DrawToBitmap(bitmap, new Rectangle(Point.Empty, login.ClientSize));
            AssertNotBlank(bitmap);
            bitmap.Save(Path.Combine(directory, "login.png"), ImageFormat.Png);
            if (login.ShowInTaskbar || login.AcceptButton is null || login.CancelButton is null) throw new Exception("Login window contract changed");
            Console.WriteLine("PASS native login form");
            login.Close();
        }
        var account = "allinai-smoke-" + Guid.NewGuid().ToString("N") + "@example.invalid";
        var password = Guid.NewGuid().ToString("N");
        try
        {
            CredentialVault.Set(account, password);
            if (!CredentialVault.Contains(account) || CredentialVault.Get(account) != password) throw new Exception("Credential Manager round trip failed");
        }
        finally { CredentialVault.Delete(account); }
        if (CredentialVault.Contains(account)) throw new Exception("Test credential was not removed");
        Console.WriteLine("PASS Windows Credential Manager round trip and cleanup");
        foreach (var background in new[] { Color.WhiteSmoke, Color.FromArgb(28, 28, 28) })
        {
            using var bitmap = new Bitmap(360, 64);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(background);
            var x = 16;
            foreach (var content in new[] { new SummaryContent { Symbol = StatusSymbol.Quota, Progress = 1 }, states["subscription"],
                states["balance"], states["exhausted"], states["unknown"], states["signed-out"] })
            {
                using var icon = ProgressIcon.Make(content);
                graphics.DrawIcon(icon, new Rectangle(x, 16, 32, 32));
                x += 56;
            }
            bitmap.Save(Path.Combine(directory, background == Color.WhiteSmoke ? "icons-light.png" : "icons-dark.png"), ImageFormat.Png);
        }
        return 0;
    }

    private static void AssertNotBlank(Bitmap bitmap)
    {
        var colors = new HashSet<int>();
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++) colors.Add(bitmap.GetPixel(x, y).ToArgb());
        if (colors.Count < 10) throw new Exception("Native preview is empty; child controls were not rendered");
    }
}
