import { expect, openTimeline, postRow, test, titles } from './helpers';

test.describe('feed page and search', () => {
  test('a post links to its feed page, which holds only that feed', async ({ page }) => {
    await openTimeline(page);
    await postRow(page, 'Tech 30').locator('a.post-domain').click();
    await expect(page).toHaveURL(/\/posts\?href=/);
    await expect(postRow(page, 'Tech 30')).toBeVisible();
    await expect(postRow(page, 'World 30')).toHaveCount(0);
    expect((await titles(page)).every(t => t.startsWith('Tech'))).toBe(true);
  });

  test('search finds a post by a word in its title', async ({ page }) => {
    await page.goto('/search');
    await page.getByPlaceholder('Search...').fill('quantum');
    await page.getByRole('button', { name: /Search/ }).click();
    await expect(postRow(page, 'Tech 12 quantum widgets')).toBeVisible();
    expect(await titles(page)).toEqual(['Tech 12 quantum widgets']);
  });

  test('search finds a post by a word in its body', async ({ page }) => {
    await page.goto('/search');
    await page.getByPlaceholder('Search...').fill('zeppelin');
    await page.getByRole('button', { name: /Search/ }).click();
    await expect(postRow(page, 'World 15')).toBeVisible();
    expect(await titles(page)).toEqual(['World 15']);
  });

});
