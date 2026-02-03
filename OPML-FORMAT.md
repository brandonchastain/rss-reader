# OPML Import/Export Format

This RSS reader supports importing and exporting feeds in OPML 2.0 format with full support for multiple tags per feed using the standard nested outline structure.

## Format Specification

### Multiple Tags Support - Standard Nested Structure

Following the OPML 2.0 standard and common practices used by major RSS readers (Feedly, NewsBlur, Inoreader, The Old Reader), feeds with multiple tags are represented using **nested outline elements** where each tag becomes a category outline, and feeds appear under each category they belong to.

```xml
<opml version="2.0">
  <head>
    <title>RSS Feed Export</title>
  </head>
  <body>
    <outline text="tech" title="tech">
      <outline type="rss" xmlUrl="https://example.com/feed" text="Example Feed" />
    </outline>
    <outline text="news" title="news">
      <outline type="rss" xmlUrl="https://example.com/feed" text="Example Feed" />
    </outline>
    <outline text="programming" title="programming">
      <outline type="rss" xmlUrl="https://example.com/feed" text="Example Feed" />
    </outline>
  </body>
</opml>
```

**Key Points:**
- Each tag is represented as a category outline element (without `type="rss"`)
- Feeds with multiple tags appear multiple times, once under each category
- Feeds without tags appear directly under the `<body>` element
- During import, duplicate feed URLs are deduplicated and all tags are collected

### Example Export

When exporting feeds with multiple tags, the RSS reader generates OPML in this standard format:

```xml
<?xml version="1.0" encoding="UTF-8"?>
<opml version="2.0">
  <head>
    <title>RSS Feed Export</title>
    <dateCreated>Sun, 01 Feb 2026 20:00:00 GMT</dateCreated>
  </head>
  <body>
    <outline text="dotnet" title="dotnet">
      <outline type="rss" 
               xmlUrl="https://techblog.example.com/feed" 
               text="https://techblog.example.com/feed" />
    </outline>
    <outline text="programming" title="programming">
      <outline type="rss" 
               xmlUrl="https://techblog.example.com/feed" 
               text="https://techblog.example.com/feed" />
    </outline>
    <outline text="tech" title="tech">
      <outline type="rss" 
               xmlUrl="https://techblog.example.com/feed" 
               text="https://techblog.example.com/feed" />
    </outline>
    <outline text="science" title="science">
      <outline type="rss" 
               xmlUrl="https://science.example.com/feed" 
               text="https://science.example.com/feed" />
    </outline>
  </body>
</opml>
```

In this example:
- `https://techblog.example.com/feed` has 3 tags: tech, programming, dotnet
- `https://science.example.com/feed` has 1 tag: science

### Example Import

When importing OPML files, the RSS reader:

1. **Parses nested structure** - Traverses the outline hierarchy to identify categories
2. **Collects all tags** - Feeds appearing under multiple categories get all those tags
3. **Deduplicates feeds** - Same feed URL appearing multiple times is merged with all tags combined
4. **Supports legacy format** - Also handles comma-separated `category` attribute for backwards compatibility

For example, this standard OPML:
```xml
<body>
  <outline text="tech" title="tech">
    <outline type="rss" xmlUrl="https://example.com/feed" />
  </outline>
  <outline text="news" title="news">
    <outline type="rss" xmlUrl="https://example.com/feed" />
  </outline>
</body>
```

Will import one feed with two tags: `tech` and `news`.

### Backwards Compatibility

For compatibility with older exports or other tools, the importer also supports the legacy comma-separated `category` attribute:

```xml
<outline type="rss" 
         xmlUrl="https://example.com/feed" 
         category="tech,news,programming" />
```

This format is parsed and the tags are extracted, but **exports always use the standard nested structure**.

## Compatibility

This format is compatible with major RSS readers:
- **Feedly** - Uses nested outline structure
- **NewsBlur** - Uses nested outline structure
- **The Old Reader** - Uses nested outline structure
- **Inoreader** - Uses nested outline structure
- Other OPML 2.0 compliant readers

## Implementation Details

- **Export**: Creates category outline elements for each tag; feeds appear under each category they belong to
- **Import**: Recursively parses nested outline structure; deduplicates feeds by URL and collects all tags
- **Storage**: Tags are stored in a comma-separated TEXT column in the SQLite database
- **Round-trip**: Tags are fully preserved when exporting and re-importing OPML files

