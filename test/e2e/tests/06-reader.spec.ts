import { expect, openTimeline, scrollToPost, test } from './helpers';

// Each test puts back the read state it changes, so they can run in any order.
test.describe('reading view and keys', () => {
  test('the headline opens the reader in place and the original stays one click away', async ({ page }) => {
    await openTimeline(page);
    const row = await scrollToPost(page, 'Tech 21');
    const headline = row.locator('a.post-headline');
    const href = await headline.getAttribute('href');
    const url = page.url();

    await headline.click();
    const reader = page.locator('.postContent');
    await expect(reader).toBeVisible();
    expect(page.url()).toBe(url);
    await expect(reader.getByRole('link', { name: '[open original ↗]' })).toHaveAttribute('href', href!);
    await expect(row.locator('a.post-external')).toHaveAttribute('href', href!);

    // Opening marked it read. Marking it unread while open must survive closing.
    await row.getByRole('button', { name: '[mark unread]' }).click();
    await reader.getByRole('button', { name: '[collapse]' }).click();
    await expect(page.locator('.postContent')).toHaveCount(0);
    await expect(row.getByRole('button', { name: '[mark read]' })).toBeVisible();
  });

  test('j/k move, l/h open and close, m toggles read, ? lists the keys', async ({ page }) => {
    await openTimeline(page);
    const current = page.locator('#post-table .row[data-current]');

    await page.keyboard.press('j');
    await expect(current).toHaveCount(1);
    const first = await current.getAttribute('data-post-id');
    await page.keyboard.press('j');
    await expect(current).not.toHaveAttribute('data-post-id', first!);
    await page.keyboard.press('k');
    await expect(current).toHaveAttribute('data-post-id', first!);

    const wasRead = (await current.getByRole('button', { name: '[mark unread]' }).count()) > 0;
    await page.keyboard.press('l');
    await expect(page.locator('.postContent')).toBeVisible();
    await page.keyboard.press('h');
    await expect(page.locator('.postContent')).toHaveCount(0);
    await expect(current.getByRole('button', { name: '[mark unread]' })).toBeVisible();

    if (!wasRead) {
      await page.keyboard.press('m');
      await expect(current.getByRole('button', { name: '[mark read]' })).toBeVisible();
    }

    await page.keyboard.press('?');
    await expect(page.locator('#kb-help')).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(page.locator('#kb-help')).toBeHidden();
  });
});
