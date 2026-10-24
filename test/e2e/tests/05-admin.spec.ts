import type { Page } from '@playwright/test';
import { expect, test } from './helpers';

// Points on the trends chart for each range, from the snapshots the fixture seeds
// (10 min, 40 min, 12 h, 3 d, 20 d and 200 d old) before any refresh adds one.
const RANGES: Array<[range: string, title: string, points: number]> = [
  ['1h', 'Trends (1 hour)', 2],
  ['1d', 'Trends (24 hours)', 3],
  ['7d', 'Trends (7 days)', 4],
  ['30d', 'Trends (30 days)', 5],
  ['1y', 'Trends (1 year)', 6],
];

const chartPoints = (page: Page) =>
  page.evaluate(() => {
    const canvas = document.getElementById('admin-stats-chart') as any;
    return canvas?._chartInstance?.data?.datasets?.[0]?.data?.length ?? -1;
  });

const card = (page: Page, label: string) =>
  page.locator('.stat-card', { hasText: label }).locator('.stat-value');

test.describe('admin dashboard', () => {
  test('opens for the administrator and lists the users', async ({ page }) => {
    await page.goto('/admin');
    await expect(page.getByRole('heading', { name: 'Admin Dashboard' })).toBeVisible();
    await expect(card(page, 'Users')).toHaveText('1');
    await expect(card(page, 'Feeds')).toHaveText('4');
    await expect(page.locator('.users-table')).toContainText('test-user-id');
  });

  test('the range switch changes the chart', async ({ page }) => {
    await page.goto('/admin');
    await expect(page.getByRole('heading', { name: 'Trends (30 days)' })).toBeVisible();
    for (const [range, title, points] of RANGES) {
      const button = page.getByRole('button', { name: range, exact: true });
      await button.click();
      await expect(button).toHaveAttribute('aria-pressed', 'true');
      await expect(page.getByRole('heading', { name: title })).toBeVisible();
      await expect.poll(() => chartPoints(page), { message: `points for ${range}` }).toBe(points);
      for (const [other] of RANGES.filter(r => r[0] !== range)) {
        await expect(page.getByRole('button', { name: other, exact: true })).toHaveAttribute('aria-pressed', 'false');
      }
    }
  });

  test('Refresh now takes a fresh snapshot of the real counts', async ({ page }) => {
    await page.goto('/admin');
    // The seeded latest snapshot claims 100 posts; the database holds 69.
    await expect(card(page, 'Posts')).toHaveText('100');
    const refresh = page.waitForResponse(
      r => r.url().includes('/api/admin/stats/refresh') && r.request().method() === 'POST');
    await page.getByRole('button', { name: 'Refresh now' }).click();
    expect((await refresh).status()).toBe(200);
    await expect(card(page, 'Posts')).toHaveText('69');
    await expect(page.getByRole('button', { name: 'Refresh now' })).toBeEnabled();
  });
});
