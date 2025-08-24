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

            string rootDomain = new Uri(url).Authority.ToLowerInvariant();

            string prefix = "www.";
            if (rootDomain.StartsWith(prefix))
            {
                rootDomain = rootDomain.Substring(prefix.Length);
            }

            return rootDomain;
        }
    }
}