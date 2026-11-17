import { expect, openTimeline, test } from './helpers';

const cacheKeys = (page: import('@playwright/test').Page) =>
  page.evaluate(() => Object.keys(localStorage).filter(k => k.startsWith('rssApp.cache.')));

test.describe('privacy', () => {
  test('the browser cache is keyed by account id, and email-keyed slots are swept', async ({ page }) => {
    // A slot in the old shape, keyed by an email, left over from before the change.
    await page.addInitScript(() => {
      if (!sessionStorage.getItem('e2e.seeded')) {
        localStorage.setItem('rssApp.cache.v1.person@example.com.user', '{}');
        sessionStorage.setItem('e2e.seeded', '1');
      }
    });

    await openTimeline(page);

    await expect.poll(() => cacheKeys(page)).toContainEqual(expect.stringMatching(/^rssApp\.cache\.v2\.\d+\.timeline\./));
    const keys = await cacheKeys(page);
    expect(keys.filter(k => k.includes('@'))).toEqual([]);
    expect(keys.filter(k => !/^rssApp\.cache\.v2\.\d+\./.test(k))).toEqual([]);
  });
});
