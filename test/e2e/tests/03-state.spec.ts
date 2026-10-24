import { expect, openTimeline, postRow, scrollToPost, setFilter, test } from './helpers';

// These change data, each on its own posts, and put it back, so they can run in any order.
test.describe('read, saved and tag state', () => {
  test('mark read sticks across a reload, and mark unread undoes it', async ({ page }) => {
    await openTimeline(page);
    const row = await scrollToPost(page, 'Tech 20');
    await expect(row.getByRole('button', { name: '[mark read]' })).toBeVisible();
    await row.getByRole('button', { name: '[mark read]' }).click();
    await expect(row.getByRole('button', { name: '[mark unread]' })).toBeVisible();

    await page.reload();
    const reloaded = await scrollToPost(page, 'Tech 20');
    await expect(reloaded.getByRole('button', { name: '[mark unread]' })).toBeVisible();

    await reloaded.getByRole('button', { name: '[mark unread]' }).click();
    await expect(reloaded.getByRole('button', { name: '[mark read]' })).toBeVisible();
    await page.reload();
    const again = await scrollToPost(page, 'Tech 20');
    await expect(again.getByRole('button', { name: '[mark read]' })).toBeVisible();
  });

  test('save adds a post to the saved view and unsave removes it', async ({ page }) => {
    await openTimeline(page);
    const row = await scrollToPost(page, 'World 20');
    await row.getByRole('button', { name: '[save]' }).click();
    await expect(row.getByRole('button', { name: '[unsave]' })).toBeVisible();

    await page.reload();
    await setFilter(page, '[saved]', true);
    await expect(postRow(page, 'World 20')).toBeVisible();
    await expect(postRow(page, 'World 07')).toBeVisible();

    await postRow(page, 'World 20').getByRole('button', { name: '[unsave]' }).click();
    await page.reload();
    await setFilter(page, '[saved]', true);
    await expect(postRow(page, 'World 07')).toBeVisible();
    await expect(postRow(page, 'World 20')).toHaveCount(0);
    await setFilter(page, '[saved]', false);
  });

  test('hiding a tag removes its feed from the timeline until it is shown again', async ({ page }) => {
    await page.goto('/tags');
    const news = page.locator('.tags-row', { has: page.locator('.post-tag', { hasText: /^news$/ }) });
    await expect(news.locator('.tags-status')).toHaveText('Visible');
    await news.getByRole('button', { name: 'Hide' }).click();
    await expect(news.locator('.tags-status')).toHaveText('Hidden from timeline');

    await openTimeline(page);
    await expect(postRow(page, 'Tech 30')).toBeVisible();
    await expect(postRow(page, 'World 30')).toHaveCount(0);

    await page.goto('/tags');
    await page
      .locator('.tags-row', { has: page.locator('.post-tag', { hasText: /^news$/ }) })
      .getByRole('button', { name: 'Show' })
      .click();
    await openTimeline(page);
    await expect(postRow(page, 'World 30')).toBeVisible();
  });
});
