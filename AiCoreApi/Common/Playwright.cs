namespace AiCoreApi.Common
{
    public class PlaywrightInstall
    {
        private static bool _playwrightInstalled;
        private static readonly object Lock = new();
        public static void EnsureInstalled()
        {
            if (_playwrightInstalled)
                return;
            lock (Lock)
            {
                if (_playwrightInstalled)
                    return;
                Microsoft.Playwright.Program.Main(new[] { "install", "--with-deps" });
                _playwrightInstalled = true;
            }
        }
    }
}
