# OPML Import/Export Format

This RSS reader supports importing and exporting feeds in OPML 2.0 format.

## Basic Format

The implementation follows the [OPML 2.0 specification](https://opml.org/spec2.opml) with standard RSS outline elements:

```xml
<?xml version="1.0" encoding="UTF-8"?>
<opml version="2.0">
  <head>
    <title>RSS Feed Export</title>
    <dateCreated>Mon, 03 Feb 2026 06:00:00 GMT</dateCreated>
  </head>
  <body>
    <outline type="rss" 
             xmlUrl="https://example.com/feed" 
             text="Example Feed"
             category="tech,news,programming" />
  </body>
</opml>
```

### Required Attributes
- `type="rss"` - Identifies the outline as an RSS feed
- `xmlUrl` - The HTTP address of the feed
- `text` - User-editable text (typically the feed title)

### Optional Attributes
- `category` - Comma-separated list of tags/categories

## Multiple Tags

Per the OPML 2.0 spec, multiple tags are represented as comma-separated values in the `category` attribute. For simple (non-hierarchical) tags, no slashes are used.

**Example:**
```xml
<outline type="rss" xmlUrl="https://example.com/feed" text="Feed" category="tech,news" />
```

The import parser splits on commas and trims whitespace from each tag.
