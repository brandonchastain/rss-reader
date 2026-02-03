# OPML Import/Export Format

This RSS reader supports importing and exporting feeds in OPML 2.0 format with full support for multiple tags per feed using the **comma-separated category attribute** as specified in the OPML 2.0 specification.

## Format Specification

### Multiple Tags Support - OPML 2.0 Compliant

According to the [OPML 2.0 specification](https://opml.org/spec2.opml):

> **category** is a string of comma-separated slash-delimited category strings, in the format defined by the RSS 2.0 category element. To represent a "tag," the category string should contain no slashes.

For simple tags (non-hierarchical categories), the RSS reader uses **comma-separated values** in the `category` attribute:

```xml
<outline type="rss" 
         xmlUrl="https://example.com/feed" 
         text="Example Feed"
         category="tech,news,programming" />
```

**Key Points:**
- Tags are represented as comma-separated values in the `category` attribute
- For simple tags, no slashes are used (slashes indicate hierarchical categories like "/Boston/Weather")
- Each feed appears once with all its tags in the category attribute
- Feeds without tags have no category attribute

### Example Export

When exporting feeds with multiple tags, the RSS reader generates OPML in this format:

```xml
<?xml version="1.0" encoding="UTF-8"?>
<opml version="2.0">
  <head>
    <title>RSS Feed Export</title>
    <dateCreated>Mon, 03 Feb 2026 06:00:00 GMT</dateCreated>
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
    <outline type="rss" 
             xmlUrl="https://news.example.com/feed" 
             text="https://news.example.com/feed" />
  </body>
</opml>
```

In this example:
- `https://techblog.example.com/feed` has 3 tags: tech, programming, dotnet
- `https://science.example.com/feed` has 2 tags: science, research
- `https://news.example.com/feed` has no tags (no category attribute)

### Example Import

When importing OPML files, the RSS reader:

1. **Parses the category attribute** - Splits on commas to extract all tags
2. **Trims whitespace** - Removes extra spaces around tag names
3. **Preserves all tags** - Adds all tags to the feed's Tags collection

For example, this OPML:
```xml
<outline type="rss" 
         xmlUrl="https://example.com/feed" 
         category="tech, news , programming" />
```

Will import one feed with three tags: `tech`, `news`, and `programming` (with spaces trimmed).

## OPML 2.0 Validation

The implementation follows the OPML 2.0 specification:

### Required Elements
- `<opml>` root element with `version="2.0"` attribute
- `<head>` element containing `<title>`
- `<body>` element containing `<outline>` elements

### Required Attributes for RSS Feeds
- `type="rss"` - Identifies the outline as an RSS feed
- `xmlUrl` - The HTTP address of the feed (must not be empty)
- `text` - User-editable text (typically the feed title)

### Optional Attributes
- `category` - Comma-separated list of tags/categories

## Compatibility

This format is the **standard OPML 2.0 format** and is compatible with:
- Feed readers that support OPML 2.0 specification
- RSS aggregators that follow the OPML standard
- Any tool that correctly implements the OPML 2.0 category attribute

### Examples from OPML 2.0 Spec:
- Simple tag: `category="tech"`
- Multiple tags: `category="tech,news,programming"`
- Hierarchical category: `category="/Boston/Weather"`
- Mixed: `category="/Harvard/Berkman,/Politics"`

## Implementation Details

- **Export**: All tags for each feed are joined with commas and written to the `category` attribute
- **Import**: The `category` attribute is split by commas, and each tag is trimmed and added to the feed
- **Storage**: Tags are stored in a comma-separated TEXT column in the SQLite database
- **Round-trip**: Tags are fully preserved when exporting and re-importing OPML files
- **Validation**: OPML output is validated to ensure compliance with OPML 2.0 specification

