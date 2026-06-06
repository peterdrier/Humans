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
  loginAsEventsAdmin,
  loginAsStoreAdmin,
  loginAsCantinaAdmin,
  loginAsEETeamAdmin,
  loginAsBarrioLead,
} from '../helpers/auth';

/**
 * Top-nav visibility matrix — verifies that each role sees exactly the correct
 * policy-gated items in the top navbar.
 *
 * Post peterdrier/Humans#349 the 9 dark-orange admin items were collapsed into
 * a single composite-gated `Admin` link that opens the admin shell at `/Admin`.
 * Only two top-nav items are role/policy gated:
 *
 *   Volunteer  -> AppAccess (UserState == Active)
 *   Admin      → AnyAdminRole (composite: Admin, Board, HumanAdmin, TeamsAdmin,
 *                CampAdmin, TicketAdmin, FeedbackAdmin, FinanceAdmin, StoreAdmin,
 *                NoInfoAdmin, VolunteerCoordinator, ConsentCoordinator)
 *
 * Sidebar coverage for items inside `/Admin` lives in admin-shell.spec.ts.
 *
 * Note on "Volunteer" (Shifts) visibility:
 * The single `AppAccess` gate succeeds when UserState == Active (the user entered their
 * legal name). There is no separate shift access. Dev personas that see "Volunteer" do
 * so because their seeded UserState is Active.
 */

type NavItem = 'volunteer' | 'admin';

const ALL_NAV_ITEMS: NavItem[] = ['volunteer', 'admin'];

function getNavLocators(nav: Locator): Record<NavItem, Locator> {
  // Scope to ul.navbar-nav to exclude the navbar brand (also named "Humans")
  const items = nav.locator('ul.navbar-nav');
  return {
    volunteer: items.getByRole('link', { name: 'Volunteer', exact: true }),
    admin: items.getByRole('link', { name: 'Admin', exact: true }),
  };
}

interface RoleTest {
  name: string;
  login: (page: Page) => Promise<void>;
  visible: NavItem[];
}

// "Volunteer" (Shifts) is gated by AppAccess = UserState.Active. The authenticated dev personas
// expected to see it below are seeded Active.
//
// Admin top-nav link visibility (AnyAdminRole composite):
//   admin, board, humanAdmin, teamsAdmin, campAdmin, ticketAdmin, feedbackAdmin,
//   financeAdmin, noInfoAdmin, volunteerCoordinator, consentCoordinator
//   (StoreAdmin is in the policy but no dev login helper exists for it.)
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
    visible: ['volunteer', 'admin'],
  },
  {
    name: 'board',
    login: loginAsBoard,
    visible: ['volunteer', 'admin'],
  },
  {
    name: 'humanAdmin',
    login: loginAsHumanAdmin,
    visible: ['volunteer', 'admin'],
  },
  {
    name: 'teamsAdmin',
    login: loginAsTeamsAdmin,
    visible: ['volunteer', 'admin'],
  },
  {
    name: 'ticketAdmin',
    login: loginAsTicketAdmin,
    visible: ['volunteer', 'admin'],
  },
  {
    name: 'campAdmin',
    login: loginAsCampAdmin,
    visible: ['volunteer', 'admin'],
  },
  {
    name: 'consentCoordinator',
    login: loginAsConsentCoordinator,
    visible: ['volunteer', 'admin'],
  },
  {
    name: 'feedbackAdmin',
    login: loginAsFeedbackAdmin,
    visible: ['volunteer', 'admin'],
  },
  {
    name: 'noInfoAdmin',
    login: loginAsNoInfoAdmin,
    visible: ['volunteer', 'admin'],
  },
  {
    name: 'financeAdmin',
    login: loginAsFinanceAdmin,
    visible: ['volunteer', 'admin'],
  },
  {
    name: 'volunteerCoordinator',
    login: loginAsVolunteerCoordinator,
    visible: ['volunteer', 'admin'],
  },
  {
    name: 'eventsAdmin',
    login: loginAsEventsAdmin,
    visible: ['volunteer', 'admin'],
  },
  {
    name: 'storeAdmin',
    login: loginAsStoreAdmin,
    visible: ['volunteer', 'admin'],
  },
  {
    name: 'cantinaAdmin',
    login: loginAsCantinaAdmin,
    visible: ['volunteer', 'admin'],
  },
  // EETeamAdmin is Active but is NOT in the AnyAdminRole composite, so it does
  // NOT see the Admin top-nav.
  {
    name: 'eeTeamAdmin',
    login: loginAsEETeamAdmin,
    visible: ['volunteer'],
  },
  // Camp lead: no governance role; access via UserState.Active. Sees Volunteer, not Admin.
  {
    name: 'barrioLead',
    login: loginAsBarrioLead,
    visible: ['volunteer'],
  },
];

test.describe('Top-nav visibility matrix (#604)', () => {
  for (const role of roles) {
    test(`${role.name}: sees correct top-nav items`, async ({ page }) => {
      await role.login(page);
      await page.goto('/');

      const nav = page.locator('nav').first();
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
