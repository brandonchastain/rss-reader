# Plan: Add Animated Loading Spinner

The static "Loading..." text in [PostTable.razor](src/WasmApp/Pages/Detail/PostTable.razor#L29-L34) should be replaced with a centered animated spinner. No new CSS is needed — the project already has multiple spinner options ready to use.

**The existing `.spinner` class** in [app.css](src/WasmApp/wwwroot/css/app.css#L369-L375) defines a 50×50px circular spinner with a blue rotating border and `@keyframes spin` animation. Bootstrap 5 utility classes (`d-flex`, `justify-content-center`, `align-items-center`) are also available for centering.

**Steps**

1. In [PostTable.razor](src/WasmApp/Pages/Detail/PostTable.razor#L29-L34), replace the static loading markup:
   ```razor
   <div>
       <div>
           <span class="">Loading...</span>
       </div>
   </div>
   ```
   with a vertically/horizontally centered spinner using the existing `.spinner` class from `app.css` and Bootstrap flex utilities for centering. The container will use `vh`-based min-height so the spinner appears centered on the visible screen, not just at the top of an empty div.


DO NOT change any logic about when the Loading state is triggered or cleared. I want this change to be purely visual, so the existing `isLoading` logic will remain intact. The spinner will simply replace the "Loading..." text whenever `isLoading` is true.

This spinner will NOT show up after user clicks the "refresh/sync" button. It only shows up on initial page load, which is particularly important during ACA cold start of the backend API.

**Verification**
- Deploy the app using `swa build; swa deploy --env production` from C:\dev\rssreader\rss-reader.
- Navigate to rss.brandonchastain.com/timeline using Firefox browser. I'm already logged in.
- Observe the loading spinner appears centered while the posts are being fetched.


**Decisions**
- Using the existing custom `.spinner` from `app.css` rather than Bootstrap's `spinner-border` — it's already styled to match the app's design (blue accent color `#3498db`). Not using the full-screen `.loading-overlay` since this is inline content, not a modal overlay.
- No new CSS file or component needed — everything required already exists.
