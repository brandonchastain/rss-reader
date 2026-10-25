import { readFileSync } from 'node:fs';
import type { Page } from '@playwright/test';
import { expect, openTimeline, postRow, scrollToPost, test } from './helpers';

const feedRow = (page: Page, host: string) =>
  page.locator('.feeds-row', { has: page.locator(`a.feeds-row-link[href*="${host}"]`) });

test.describe('feeds page', () => {
  test('lists the subscribed feeds with their tags', async ({ page }) => {
    await page.goto('/feeds/mine');
    await expect(page.locator('.feeds-row')).toHaveCount(4);
    await expect(feedRow(page, 'tech.example.com').locator('.post-tag')).toHaveText('tech');
    await expect(feedRow(page, 'world.example.com').locator('.post-tag')).toHaveText('news');
    await expect(feedRow(page, 'disposable.example.com').locator('.post-tag')).toHaveCount(0);
  });

  test('the hub links to every feeds view', async ({ page }) => {
    await page.goto('/feeds');
    for (const name of ['My feeds', 'Popular feeds', 'Subscribe to a feed', 'Import / Export', 'Tags']) {
      await expect(page.locator('.feeds-card', { hasText: name })).toBeVisible();
    }
    await page.locator('.feeds-card', { hasText: 'My feeds' }).click();
    await expect(page).toHaveURL(/\/feeds\/mine$/);
    await expect(page.locator('.feeds-row')).toHaveCount(4);
  });

  test('refuses a feed address on the servers own network', async ({ page }) => {
    await page.goto('/feeds/add');
    await page.locator('.feeds-url-input').fill('http://127.0.0.1/feed.xml');
    await page.getByRole('button', { name: 'Subscribe' }).click();
    await expect(page.getByText('is not a public address')).toBeVisible();
    await page.goto('/feeds/mine');
    await expect(page.locator('.feeds-row')).toHaveCount(4);
  });

  test('asks for an address when the field is empty', async ({ page }) => {
    await page.goto('/feeds/add');
    await page.getByRole('button', { name: 'Subscribe' }).click();
    await expect(page.getByText('Feed URL is empty')).toBeVisible();
  });

  test('exports OPML that carries each feed and its tags', async ({ page }) => {
    await page.goto('/feeds/mine');
    await expect(page.locator('.feeds-row')).toHaveCount(4);
    await page.getByRole('link', { name: '← Feeds' }).click();
    await page.getByRole('link', { name: /Import \/ Export/ }).click();
    const download = page.waitForEvent('download');
    await page.getByRole('button', { name: 'Export OPML' }).click();
    const file = await download;
    expect(file.suggestedFilename()).toBe('feeds.opml');
    const opml = readFileSync(await file.path(), 'utf-8');
    const outline = (host: string) => opml.split('<outline').find(o => o.includes(host)) ?? '';
    expect(outline('tech.example.com')).toContain('xmlUrl="https://tech.example.com/feed.xml"');
    expect(outline('tech.example.com')).toContain('category="tech"');
    expect(outline('world.example.com')).toContain('category="news"');
    expect(outline('muted.example.com')).toContain('category="muted"');
    expect(outline('disposable.example.com')).not.toContain('category');
  });

  test('adds a tag and removes it again', async ({ page }) => {
    await page.goto('/feeds/mine');
    const row = feedRow(page, 'disposable.example.com');
    await row.locator('.feeds-tag-input').fill('temp');
    await row.locator('.feeds-tag-input').press('Enter');
    await expect(row.locator('.post-tag')).toHaveText('temp');
    await page.screenshot({ path: 'test-results/feeds-page.png', fullPage: true });

    await row.getByRole('button', { name: 'Remove tag temp' }).click();
    await expect(row.locator('.post-tag')).toHaveCount(0);

    await page.reload();
    await expect(feedRow(page, 'disposable.example.com').locator('.post-tag')).toHaveCount(0);
  });

  // Last in the suite: it removes the feed that the earlier tests scroll down to.
  test('unsubscribing from a feed removes it and its posts, and stays removed', async ({ page }) => {
    await openTimeline(page);
    await scrollToPost(page, 'Disposable 1');

    await page.goto('/feeds/mine');
    await feedRow(page, 'disposable.example.com').getByRole('button', { name: 'Unsubscribe' }).click();
    await feedRow(page, 'disposable.example.com').getByRole('button', { name: 'Confirm' }).click();
    await expect(feedRow(page, 'disposable.example.com')).toHaveCount(0);
    await expect(page.locator('.feeds-row')).toHaveCount(3);

    await page.reload();
    await expect(page.locator('.feeds-row')).toHaveCount(3);
    await expect(feedRow(page, 'disposable.example.com')).toHaveCount(0);

    await openTimeline(page);
    await expect(postRow(page, 'Tech 30')).toBeVisible();
    await page.mouse.wheel(0, 20000);
    await page.waitForTimeout(1500);
    await expect(page.locator('a[data-article-link]', { hasText: /^Disposable/ })).toHaveCount(0);
  });
});
