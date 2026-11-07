namespace RssReader.Shared.Extensions
{
    public static class StringExtensions
    {
        // Second-level labels that sit under a two-letter country code as part
        // of the public suffix, as in bbc.co.uk or abc.net.au. Without this the
        // last two labels of feeds.bbci.co.uk are "co.uk", which names no site.
        private static readonly HashSet<string> CountrySecondLevels = new(StringComparer.Ordinal)
        {
            "co", "com", "net", "org", "gov", "ac", "edu", "ne", "or", "go",
        };

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

            var parts = rootDomain.Split('.');
            var keep = 2;
            if (parts.Length > 2
                && parts[^1].Length == 2
                && CountrySecondLevels.Contains(parts[^2]))
            {
                keep = 3;
            }

            if (parts.Length > keep)
            {
                rootDomain = string.Join('.', parts.Skip(parts.Length - keep));
            }

            return rootDomain;
        }
    }
}
