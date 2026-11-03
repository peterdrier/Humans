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
    def delta(self, before, after, path=PATH):
        def content(args):
            return before if args[1].startswith("base:") else after

        def files(ref, roots):
            return [path] if any(path.startswith(root + "/") for root in roots) else []

        with patch.object(report, "git_files", side_effect=files), \
                patch.object(report, "try_run_git", side_effect=content):
            return report.interface_delta("base", "head")

    def test_base_interfaces_and_added_members_are_reported(self):
        for path in ("src/Humans.Base/IPlatform.cs", "src/Humans.Base/Interfaces/Metering/IPlatform.cs"):
            with self.subTest(path=path):
                before = "public interface IPlatform\n{\n}"
                after = before.replace("\n}", "\n    void Start();\n    bool IsReady { get; }\n}")
                self.assertEqual(self.delta("", after, path)["new_interfaces"],
                                 [f"IPlatform ({path})"])
                delta = self.delta(before, after, path)
                self.assertEqual(delta["added_interface_methods"], {"IPlatform": ["void Start()"]})
                self.assertEqual(delta["added_interface_properties"],
                                 {"IPlatform": ["bool IsReady { get; }"]})
                self.assertEqual(self.delta("", after.replace("public interface", "internal interface"), path)[
                    "new_interfaces"], [])

    def test_internal_contract_interfaces_do_not_add_public_surface(self):
        for visibility in ("internal ", ""):
            with self.subTest(visibility=visibility):
                before = visibility + "interface IStripeService\n{\n}"
                after = before.replace("\n}", "\n    Task WriteAsync();\n    bool IsConfigured { get; }\n}")
                delta = self.delta(before, after)
                self.assertEqual(delta["new_interfaces"], [])
                self.assertEqual(delta["added_interface_methods"], {})
                self.assertEqual(delta["added_interface_properties"], {})
                self.assertEqual(self.delta("", after)["new_interfaces"], [])

    def test_promoting_internal_interface_reports_new_public_surface(self):
        before = "internal interface IStripeService\n{\n    Task WriteAsync();\n}"
        after = before.replace("internal interface", "public interface")
        self.assertEqual(self.delta(before, after)["new_interfaces"],
                         [f"IStripeService ({PATH})"])

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

    def test_expression_body_change_does_not_add_a_method(self):
        before = """public interface IStripeService
{
    public sealed bool IsConfigured(bool enabled) => enabled;
}
"""
        after = before.replace("=> enabled;", "=> !enabled;")
        delta = self.delta(before, after)
        self.assertEqual(delta["added_interface_methods"], {})

    def test_block_body_method_reports_only_its_declaration(self):
        before = "public interface IStripeService\n{\n}"
        after = """public interface IStripeService
{
    bool IsConfigured(bool enabled)
    {
        if (enabled)
        {
            return true;
        }
        return false;
    }
}
"""
        self.assertEqual(self.delta(before, after)["added_interface_methods"],
                         {"IStripeService": ["bool IsConfigured(bool enabled)"]})
        self.assertEqual(self.delta(after, after.replace("return true;", "return false;"))[
            "added_interface_methods"], {})

    def test_body_delimiters_in_default_strings_remain_in_the_signature(self):
        before = "public interface IStripeService\n{\n}"
        after = '''public interface IStripeService
{
    string Read(string key = "a=>b{") => key;
    bool IsConfigured();
}
'''
        self.assertEqual(self.delta(before, after)["added_interface_methods"],
                         {"IStripeService": ["bool IsConfigured()", 'string Read(string key = "a=>b{")']})

    def test_comment_markers_in_default_urls_do_not_hide_interface_members(self):
        before = "public interface IStripeService\n{\n}"
        for literal in ('"https://example.org/a"', '@"https://example.org/a"'):
            with self.subTest(literal=literal):
                after = ("public interface IStripeService\n{\n"
                         f"    string Read(string url = {literal}); // read URL\n"
                         "    bool IsConfigured(); // another member\n}")
                self.assertEqual(self.delta(before, after)["added_interface_methods"],
                                 {"IStripeService": ["bool IsConfigured()",
                                                     f"string Read(string url = {literal})"]})

    def test_bodied_default_properties_report_their_declaration(self):
        before = "public interface IStripeService\n{\n}"
        after = ("public interface IStripeService\n{\n    int Count => 0;\n"
                 "    bool IsConfigured { get => true; }\n}")
        self.assertEqual(self.delta(before, after)["added_interface_properties"],
                         {"IStripeService": ["bool IsConfigured", "int Count"]})
        self.assertEqual(self.delta(after, after.replace("=> 0", "=> 1"))[
            "added_interface_properties"], {})

    def test_removed_property_and_other_type_do_not_add_surface(self):
        before = "public interface IStripeService\n{\n    bool IsConfigured { get; }\n}"
        after = "public interface IStripeService\n{\n}\npublic class Detail\n{\n    string Name { get; set; }\n}"
        delta = self.delta(before, after)
        self.assertEqual(delta["added_interface_properties"], {})
        self.assertIn("No new interfaces or interface members", report.interface_delta_markdown(delta))


if __name__ == "__main__":
    unittest.main()
