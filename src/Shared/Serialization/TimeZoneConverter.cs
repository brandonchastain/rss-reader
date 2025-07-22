namespace RssApp.Serialization;

/// <summary>
/// Provides utilities for handling timezone abbreviations and conversions
/// </summary>
public static class TimeZoneConverter
{
    /// <summary>
    /// Maps common timezone abbreviations to their UTC offsets
    /// </summary>
    public static readonly Dictionary<string, string> TimeZoneAbbreviationMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // North American Time Zones
        { "EST", "-0500" }, // Eastern Standard Time
        { "EDT", "-0400" }, // Eastern Daylight Time
        { "CST", "-0600" }, // Central Standard Time
        { "CDT", "-0500" }, // Central Daylight Time
        { "MST", "-0700" }, // Mountain Standard Time
        { "MDT", "-0600" }, // Mountain Daylight Time
        { "PST", "-0800" }, // Pacific Standard Time
        { "PDT", "-0700" }, // Pacific Daylight Time
        { "AKST", "-0900" }, // Alaska Standard Time
        { "AKDT", "-0800" }, // Alaska Daylight Time
        { "HST", "-1000" }, // Hawaii Standard Time
        { "HAST", "-1000" }, // Hawaii-Aleutian Standard Time
        { "HADT", "-0900" }, // Hawaii-Aleutian Daylight Time
        
        // European Time Zones
        { "GMT", "+0000" }, // Greenwich Mean Time
        { "BST", "+0100" }, // British Summer Time
        { "IST", "+0100" }, // Irish Standard Time
        { "WET", "+0000" }, // Western European Time
        { "WEST", "+0100" }, // Western European Summer Time
        { "CET", "+0100" }, // Central European Time
        { "CEST", "+0200" }, // Central European Summer Time
        { "EET", "+0200" }, // Eastern European Time
        { "EEST", "+0300" }, // Eastern European Summer Time
        
        // Australian Time Zones
        { "AWST", "+0800" }, // Australian Western Standard Time
        { "ACST", "+0930" }, // Australian Central Standard Time
        { "AEST", "+1000" }, // Australian Eastern Standard Time
        { "ACDT", "+1030" }, // Australian Central Daylight Time
        { "AEDT", "+1100" }, // Australian Eastern Daylight Time
        
        // Asian Time Zones
        { "HKT", "+0800" }, // Hong Kong Time
        { "JST", "+0900" }, // Japan Standard Time
        { "KST", "+0900" }, // Korea Standard Time
//        { "IST", "+0530" }, // Indian Standard Time (conflicts with Irish, but more commonly used)
//        { "CST", "+0800" }, // China Standard Time (conflicts with Central, but we prioritize North American usage)
        
        // South American Time Zones
        { "ART", "-0300" }, // Argentina Time
        { "BRT", "-0300" }, // Brasilia Time
        { "BRST", "-0200" }, // Brasilia Summer Time
        
        // New Zealand Time Zones
        { "NZST", "+1200" }, // New Zealand Standard Time
        { "NZDT", "+1300" }, // New Zealand Daylight Time
        
        // Middle Eastern Time Zones
 //       { "IST", "+0300" }, // Israel Standard Time (conflicts with others, but included for completeness)
 //       { "AST", "+0300" }, // Arabia Standard Time (conflicts with Atlantic, but included for completeness)
        
        // Misc Time Zones
        { "UTC", "+0000" }, // Coordinated Universal Time
        { "Z", "+0000" }, // Zulu Time (Military/NATO)
        { "UT", "+0000" }, // Universal Time
    };

    /// <summary>
    /// Converts a timezone abbreviation to its standard UTC offset format
    /// </summary>
    /// <param name="dateString">Date string possibly containing a timezone abbreviation</param>
    /// <returns>Date string with timezone abbreviation replaced with standard UTC offset</returns>
    public static string ConvertTimeZoneAbbreviation(string dateString)
    {
        if (string.IsNullOrWhiteSpace(dateString))
            return dateString;

        foreach (var timeZone in TimeZoneAbbreviationMap)
        {
            // Match timezone abbreviation at the end of the string with a space before it
            string pattern = $" {timeZone.Key}$";
            if (System.Text.RegularExpressions.Regex.IsMatch(dateString, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                dateString = System.Text.RegularExpressions.Regex.Replace(
                    dateString, 
                    pattern, 
                    $" {timeZone.Value}", 
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase
                );
                break;
            }
        }

        return dateString;
    }
}