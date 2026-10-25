namespace RssApp.Contracts;

/// <summary>A read-only table the admin dashboard drills into from a stat card.</summary>
public class AdminTable
{
    public string Title { get; set; } = "";
    /// <summary>True when the first column is an id the admin may delete.</summary>
    public bool Deletable { get; set; }
    public string Note { get; set; } = "";
    public List<string> Columns { get; set; } = new();
    public List<List<string>> Rows { get; set; } = new();
}
