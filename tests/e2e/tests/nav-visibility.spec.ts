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
 * Top-nav visibility matrix — verifies that each role sees exactly the correct
 * policy-gated items in the top navbar.
 *
 * Post peterdrier/Humans#349 the 9 dark-orange admin items were collapsed into
 * a single composite-gated `Admin` link that opens the admin shell at `/Admin`.
 * Only two top-nav items are role/policy gated:
 *
 *   Volunteer  → ActiveMemberOrShiftAccess
 *   Admin      → AnyAdminRole (composite: Admin, Board, HumanAdmin, TeamsAdmin,
 *                CampAdmin, TicketAdmin, FeedbackAdmin, FinanceAdmin, StoreAdmin,
 *                NoInfoAdmin, VolunteerCoordinator, ConsentCoordinator)
 *
 * Sidebar coverage for items inside `/Admin` lives in admin-shell.spec.ts.
 *
 * Note on "Volunteer" (Shifts) visibility:
 * ActiveMemberOrShiftAccess succeeds via ActiveMember claim (Volunteers team
 * membership) OR via role checks (Admin/Board/TeamsAdmin/NoInfoAdmin/VolunteerCoordinator).
 * Dev personas may not have Volunteers team membership if seeded before the current
 * DevLoginController code, so we only assert "Volunteer" for roles that guarantee it
 * via role checks. The volunteer/coordinator personas always have it since they're
 * always seeded into the Volunteers team.
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

// Volunteer (Shifts) visibility by role path:
//   ActiveMember claim: volunteer, coordinator (always seeded into Volunteers team)
//   IsTeamsAdminBoardOrAdmin: admin, board, teamsAdmin
//   ShiftRoleChecks.CanAccessDashboard: admin, noInfoAdmin, volunteerCoordinator
//
// Admin top-nav link visibility (AnyAdminRole composite):
//   admin, board, humanAdmin, teamsAdmin, campAdmin, ticketAdmin, feedbackAdmin,
//   financeAdmin, noInfoAdmin, volunteerCoordinator, consentCoordinator
//   (StoreAdmin is in the policy but no dev login helper exists for it.)
//
// Roles without a role-based "Volunteer" path only see it if they happen to have
// ActiveMember claim — environment-dependent, so we omit it from those expectations.
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
    visible: ['admin'],
  },
  {
    name: 'teamsAdmin',
    login: loginAsTeamsAdmin,
    visible: ['volunteer', 'admin'],
  },
  {
    name: 'ticketAdmin',
    login: loginAsTicketAdmin,
    visible: ['admin'],
  },
  {
    name: 'campAdmin',
    login: loginAsCampAdmin,
    visible: ['admin'],
  },
  {
    name: 'consentCoordinator',
    login: loginAsConsentCoordinator,
    visible: ['admin'],
  },
  {
    name: 'feedbackAdmin',
    login: loginAsFeedbackAdmin,
    visible: ['admin'],
  },
  {
    name: 'noInfoAdmin',
    login: loginAsNoInfoAdmin,
    visible: ['volunteer', 'admin'],
  },
  {
    name: 'financeAdmin',
    login: loginAsFinanceAdmin,
    visible: ['admin'],
  },
  {
    name: 'volunteerCoordinator',
    login: loginAsVolunteerCoordinator,
    visible: ['volunteer', 'admin'],
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
