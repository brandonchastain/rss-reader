
namespace RssApp.Persistence;

public class PersistedHiddenItems
{
    private static readonly string FileName = "hidden.csv";
    private HashSet<string> hidden;
    public PersistedHiddenItems()
    {
        if (!File.Exists(FileName))
        {
            using (File.Create(FileName))
            {

            }
        }

        this.hidden = new HashSet<string>();
        var contents = File.ReadAllText(FileName);
        foreach (string hid in contents.Split(","))
        {
            this.hidden.Add(hid);
        }
    }

    public ISet<string> GetHidden()
    {
        return this.hidden;
    }

    public void HidePost(string id)
    {
        this.hidden.Add(id);
        this.Save();
    }

    public void Unhide(string id)
    {
        this.hidden.Remove(id);
        this.Save();
    }

    private void Save()
    {
        if (this.hidden.Count == 0)
        {
            return;
        }
        
        File.WriteAllText(FileName, string.Join(",", this.hidden));
    }
}