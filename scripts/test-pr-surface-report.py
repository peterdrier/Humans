#!/usr/bin/env python3
"""Bounded interface-surface fixtures; no checkout or Git mutation required."""

import importlib.util
from pathlib import Path
import unittest
from unittest.mock import patch


spec = importlib.util.spec_from_file_location(
    "surface_report", Path(__file__).with_name("pr-surface-report.py"))
report = importlib.util.module_from_spec(spec)
spec.loader.exec_module(report)

PATH = "src/Sections/Humans.Stripe/Contracts/IStripeService.cs"


class InterfaceSurfaceTests(unittest.TestCase):
    def delta(self, before, after):
        def content(args):
            return before if args[1].startswith("base:") else after

        with patch.object(report, "git_files", return_value=[PATH]), \
                patch.object(report, "try_run_git", side_effect=content):
            return report.interface_delta("base", "head")

    def test_added_property_is_reported_alongside_methods(self):
        before = "public interface IStripeService\n{\n    Task ReadAsync();\n}"
        after = before.replace("    Task", "    bool IsConfigured { get; }\n    Task")

        delta = self.delta(before, after)

        self.assertEqual(delta["added_interface_properties"],
                         {"IStripeService": ["bool IsConfigured { get; }"]})
        self.assertEqual(delta["added_interface_methods"], {})
        markdown = report.interface_delta_markdown(delta)
        self.assertIn("**Added interface properties**", markdown)
        self.assertIn("bool IsConfigured { get; }", markdown)

    def test_multiline_accessors_and_formatting(self):
        before = "public interface IStripeService\n{\n    bool IsConfigured { get; }\n}"
        after = """public interface IStripeService
{
    bool IsConfigured
    {
        get;
    }
    string Name
    {
        get;
        set;
    }
}
"""
        delta = self.delta(before, after)
        self.assertEqual(delta["added_interface_properties"],
                         {"IStripeService": ["string Name { get; set; }"]})
        self.assertEqual(delta["added_interface_methods"], {})

    def test_methods_still_include_multiline_signatures(self):
        before = "public interface IStripeService\n{\n}"
        after = """public interface IStripeService
{
    Task ReadAsync(
        string id,
        CancellationToken ct = default);
}
"""
        delta = self.delta(before, after)
        self.assertEqual(delta["added_interface_methods"],
                         {"IStripeService": ["Task ReadAsync( string id, CancellationToken ct = default)"]})
        self.assertEqual(delta["added_interface_properties"], {})

    def test_removed_property_and_other_type_do_not_add_surface(self):
        before = "public interface IStripeService\n{\n    bool IsConfigured { get; }\n}"
        after = "public interface IStripeService\n{\n}\npublic class Detail\n{\n    string Name { get; set; }\n}"
        delta = self.delta(before, after)
        self.assertEqual(delta["added_interface_properties"], {})
        self.assertIn("No new interfaces or interface members", report.interface_delta_markdown(delta))


if __name__ == "__main__":
    unittest.main()
