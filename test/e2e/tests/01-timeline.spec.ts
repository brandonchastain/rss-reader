import { expect, openTimeline, postRow, scrollToPost, test, titles } from './helpers';

test.describe('timeline', () => {
  test('shows the newest posts first, with their feed tag', async ({ page }) => {
    await openTimeline(page);
    const first = await titles(page);
    expect(first.slice(0, 4)).toEqual(['World 30', 'Tech 30', 'World 29', 'Tech 29']);
    await expect(postRow(page, 'World 30').locator('.post-tag')).toHaveText('news');
    await expect(postRow(page, 'Tech 30').locator('.post-tag')).toHaveText('tech');
  });

  test('loads older posts as you scroll', async ({ page }) => {
    await openTimeline(page);
    await expect(postRow(page, 'Disposable 1')).toHaveCount(0);
    await scrollToPost(page, 'Disposable 1');
    await expect(postRow(page, 'Disposable 1')).toBeVisible();
  });

  test('never shows posts from a feed whose tag is hidden', async ({ page }) => {
    await openTimeline(page);
    await scrollToPost(page, 'Disposable 1');
    expect((await titles(page)).filter(t => t.startsWith('Muted'))).toEqual([]);
  });

  test('read posts are marked as read', async ({ page }) => {
    await page.goto('/posts?href=' + encodeURIComponent('https://tech.example.com/feed.xml'));
    await scrollToPost(page, 'Tech 05');
    await expect(postRow(page, 'Tech 05').getByRole('button', { name: '[mark unread]' })).toBeVisible();
    await expect(postRow(page, 'Tech 06').getByRole('button', { name: '[mark read]' })).toBeVisible();
  });

  test('the unread filter hides read posts', async ({ page }) => {
    await page.goto('/posts?href=' + encodeURIComponent('https://tech.example.com/feed.xml'));
    await scrollToPost(page, 'Tech 05');
    await page.getByRole('button', { name: '[unread]' }).click();
    await scrollToPost(page, 'Tech 06');
    await expect(postRow(page, 'Tech 05')).toHaveCount(0);
    await expect(postRow(page, 'Tech 01')).toHaveCount(0);
  });

  test('the saved filter shows only saved posts', async ({ page }) => {
    await openTimeline(page);
    await page.getByRole('button', { name: '[saved]' }).click();
    await expect(postRow(page, 'Tech 07')).toBeVisible();
    await expect(postRow(page, 'World 07')).toBeVisible();
    await expect(postRow(page, 'Tech 30')).toHaveCount(0);
    expect((await titles(page)).sort()).toEqual(['Tech 07', 'World 07']);
  });

  test('a tag filter shows only that tag', async ({ page }) => {
    await openTimeline(page);
    await page.getByRole('button', { name: '[filters]' }).click();
    await page.locator('#filter-tags').getByRole('button', { name: 'tech', exact: true }).click();
    await expect(postRow(page, 'Tech 30')).toBeVisible();
    await expect(postRow(page, 'World 30')).toHaveCount(0);
    expect((await titles(page)).every(t => t.startsWith('Tech'))).toBe(true);
  });
});
