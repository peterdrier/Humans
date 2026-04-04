import { test, expect, type Page, type Locator } from '@playwright/test';
import {
  loginAsVolunteer,
  loginAsCoordinator,
  loginAsAdmin,
  loginAsBoard,
  loginAsCampAdmin,
  loginAsConsentCoordinator,
  loginAsFeedbackAdmin,
  loginAsFinanceAdmin,
  loginAsHumanAdmin,
  loginAsNoInfoAdmin,
  loginAsTeamsAdmin,
  loginAsTicketAdmin,
  loginAsVolunteerCoordinator,
} from '../helpers/auth';

/**
 * Nav visibility matrix — verifies that each role sees exactly the correct
 * policy-gated nav items. This is the primary e2e coverage for the
 * authorization policy foundation (PR #125 / #366).
 *
 * Nav items gated by authorize-policy in _Layout.cshtml:
 *   Volunteer  → ActiveMemberOrShiftAccess
 *   V          → VolunteerSectionAccess
 *   Review     → ReviewQueueAccess
 *   Voting     → BoardOrAdmin
 *   Board      → BoardOrAdmin
 *   Humans     → HumanAdminOnly
 *   Admin      → AdminOnly
 *   Google     → AdminOnly
 *   Tickets    → TicketAdminBoardOrAdmin
 *   Finance    → FinanceAdminOrAdmin
 */

type NavItem = 'volunteer' | 'v' | 'review' | 'voting' | 'board' | 'humans' | 'admin' | 'google' | 'tickets' | 'finance';

const ALL_NAV_ITEMS: NavItem[] = ['volunteer', 'v', 'review', 'voting', 'board', 'humans', 'admin', 'google', 'tickets', 'finance'];

function getNavLocators(nav: Locator): Record<NavItem, Locator> {
  // Scope to ul.navbar-nav to exclude the navbar brand (also named "Humans")
  const items = nav.locator('ul.navbar-nav');
  return {
    volunteer: items.getByRole('link', { name: 'Volunteer', exact: true }),
    v: items.getByRole('link', { name: 'V', exact: true }),
    review: items.getByRole('link', { name: /^Review/ }),
    voting: items.getByRole('link', { name: /^Voting/ }),
    board: items.getByRole('link', { name: 'Board', exact: true }),
    humans: items.getByRole('link', { name: 'Humans', exact: true }),
    admin: items.getByRole('link', { name: 'Admin', exact: true }),
    google: items.getByRole('link', { name: 'Google', exact: true }),
    tickets: items.getByRole('link', { name: 'Tickets', exact: true }),
    finance: items.getByRole('link', { name: 'Finance', exact: true }),
  };
}

interface RoleTest {
  name: string;
  login: (page: Page) => Promise<void>;
  visible: NavItem[];
}

// All dev personas are in the Volunteers team → all have ActiveMember claim → all see "Volunteer" (Shifts).
// The matrix below defines which ADDITIONAL restricted nav items each role sees.
const roles: RoleTest[] = [
  {
    name: 'volunteer',
    login: loginAsVolunteer,
    visible: ['volunteer'],
  },
  {
    name: 'coordinator',
    login: loginAsCoordinator,
    visible: ['volunteer'],
  },
  {
    name: 'admin',
    login: loginAsAdmin,
    visible: ['volunteer', 'v', 'review', 'voting', 'board', 'admin', 'google', 'tickets', 'finance'],
    // Note: 'humans' is NOT visible — HumanAdminOnly requires HumanAdmin AND NOT Admin
  },
  {
    name: 'board',
    login: loginAsBoard,
    visible: ['volunteer', 'v', 'review', 'voting', 'board', 'tickets'],
  },
  {
    name: 'humanAdmin',
    login: loginAsHumanAdmin,
    visible: ['volunteer', 'humans'],
  },
  {
    name: 'teamsAdmin',
    login: loginAsTeamsAdmin,
    visible: ['volunteer', 'v'],
  },
  {
    name: 'ticketAdmin',
    login: loginAsTicketAdmin,
    visible: ['volunteer', 'tickets'],
  },
  {
    name: 'campAdmin',
    login: loginAsCampAdmin,
    visible: ['volunteer'],
  },
  {
    name: 'consentCoordinator',
    login: loginAsConsentCoordinator,
    visible: ['volunteer', 'review'],
  },
  {
    name: 'feedbackAdmin',
    login: loginAsFeedbackAdmin,
    visible: ['volunteer'],
  },
  {
    name: 'noInfoAdmin',
    login: loginAsNoInfoAdmin,
    visible: ['volunteer'],
  },
  {
    name: 'financeAdmin',
    login: loginAsFinanceAdmin,
    visible: ['volunteer', 'finance'],
  },
  {
    name: 'volunteerCoordinator',
    login: loginAsVolunteerCoordinator,
    visible: ['volunteer', 'v', 'review'],
  },
];

test.describe('Nav visibility matrix (#366)', () => {
  for (const role of roles) {
    test(`${role.name}: sees correct nav items`, async ({ page }) => {
      await role.login(page);
      await page.goto('/');

      const nav = page.locator('nav');
      const locators = getNavLocators(nav);
      const hidden = ALL_NAV_ITEMS.filter(item => !role.visible.includes(item));

      for (const item of role.visible) {
        await expect(locators[item], `${role.name} should see '${item}'`).toBeVisible();
      }
      for (const item of hidden) {
        await expect(locators[item], `${role.name} should NOT see '${item}'`).not.toBeVisible();
      }
    });
  }
});
