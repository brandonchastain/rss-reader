# OPML Import/Export Format

This RSS reader supports importing and exporting feeds in OPML 2.0 format with full support for multiple tags per feed.

## Format Specification

### Multiple Tags Support

Tags (categories) are represented in OPML files using comma-separated values in the `category` attribute:

```xml
<outline type="rss" 
         xmlUrl="https://example.com/feed" 
         text="Example Feed"
         category="tech,news,programming" />
```

### Example Export

When exporting feeds with multiple tags, the RSS reader generates OPML in this format:

```xml
<?xml version="1.0" encoding="UTF-8"?>
<opml version="2.0">
  <head>
    <title>RSS Feed Export</title>
    <dateCreated>Sun, 01 Feb 2026 20:00:00 GMT</dateCreated>
  </head>
  <body>
    <outline type="rss" 
             xmlUrl="https://techblog.example.com/feed" 
             text="https://techblog.example.com/feed"
             category="tech,programming,dotnet" />
    <outline type="rss" 
             xmlUrl="https://science.example.com/feed" 
             text="https://science.example.com/feed"
             category="science,research" />
  </body>
</opml>
```

### Example Import

When importing OPML files, the RSS reader:

1. **Parses all tags** from the `category` attribute by splitting on commas
2. **Trims whitespace** from each tag name
3. **Preserves all tags** when saving to the database

For example, this OPML:
```xml
<outline type="rss" 
         xmlUrl="https://example.com/feed" 
         category="tech, news , programming" />
```

Will import three tags: `tech`, `news`, and `programming` (with spaces trimmed).

## Compatibility

This format is compatible with most RSS readers including:
- Feedly
- NewsBlur
- The Old Reader
- Inoreader
- And other OPML 2.0 compliant readers

## Implementation Details

- **Export**: All tags for each feed are joined with commas and written to the `category` attribute
- **Import**: The `category` attribute is split by commas, and each tag is trimmed and added to the feed
- **Storage**: Tags are stored in a comma-separated TEXT column in the SQLite database
- **Round-trip**: Tags are fully preserved when exporting and re-importing OPML files
