import { test as base, expect, type Locator, type Page } from '@playwright/test';

// Favicon lookups would go to the internet for the seeded example.com feeds and fail slowly;
// the app falls back to its placeholder image, so abort them.
export const test = base.extend({
  page: async ({ page }, use) => {
    await page.route('**/api/feed/icon**', route => route.abort());
    await use(page);
  },
});

export { expect };

/** A timeline or feed-page row, found by the exact title of its link. */
export function postRow(page: Page, title: string): Locator {
  return page
    .locator('.row[data-post-id]')
    .filter({ has: page.locator('a[data-article-link]', { hasText: new RegExp(`^${title}$`) }) });
}

export async function openTimeline(page: Page) {
  await page.goto('/timeline');
  await expect(page.locator('.row[data-post-id]').first()).toBeVisible();
}

/** Scrolls until the row for `title` appears, loading further pages on the way. */
export async function scrollToPost(page: Page, title: string): Promise<Locator> {
  const row = postRow(page, title);
  await expect(async () => {
    if (await row.count() === 0) {
      await page.mouse.wheel(0, 3000);
      await page.waitForTimeout(150);
    }
    await expect(row).toHaveCount(1, { timeout: 500 });
  }).toPass({ timeout: 30_000 });
  await row.scrollIntoViewIfNeeded();
  return row;
}

/** Turns a toolbar filter ([unread] / [saved]) on or off; the choice survives a reload, so click only when it differs. */
export async function setFilter(page: Page, name: '[unread]' | '[saved]', on: boolean) {
  // The app restores the remembered filter after it loads, so read the button only once posts show.
  await expect(page.locator('.row[data-post-id]').first()).toBeVisible();
  const button = page.getByRole('button', { name });
  const active = (await button.getAttribute('class'))?.includes('toolbar-active') ?? false;
  if (active !== on) {
    await button.click();
  }
  await expect(button).toHaveClass(on ? /toolbar-active/ : /^((?!toolbar-active).)*$/);
}

export const titles = (page: Page) => page.locator('a[data-article-link]').allTextContents();
