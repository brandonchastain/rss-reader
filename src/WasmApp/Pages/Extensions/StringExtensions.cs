namespace RssWasmApp.Pages
{
    public static class StringExtensions
    {
        public static string GetRootDomain(this string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return string.Empty;
            }

            return new Uri(url).Authority;
        }
    }
}