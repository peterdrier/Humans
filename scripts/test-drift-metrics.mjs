import test from 'node:test';
import assert from 'node:assert/strict';
import { analyzeFiles, compareSnapshots, formatMarkdown, shouldAnalyzePath } from './drift-metrics.mjs';

test('shouldAnalyzePath filters generated, migration, and build paths', () => {
  assert.equal(shouldAnalyzePath('src/Sections/Humans.Users/Services/UserService.cs'), true);
  assert.equal(shouldAnalyzePath('src/Sections/Humans.Users/Views/Index.cshtml'), true);
  assert.equal(shouldAnalyzePath('tests/Humans.Users.Tests/UserServiceTests.cs'), true);
  assert.equal(shouldAnalyzePath('memory/code/example.md'), true);
  assert.equal(shouldAnalyzePath('AGENTS.md'), true);

  assert.equal(shouldAnalyzePath('src/Sections/Humans.Users/Data/Migrations/20260101_Init.cs'), false);
  assert.equal(shouldAnalyzePath('src/Sections/Humans.Users/UsersResource.Designer.cs'), false);
  assert.equal(shouldAnalyzePath('src/Sections/Humans.Users/obj/Debug/net10.0/Foo.cs'), false);
  assert.equal(shouldAnalyzePath('docs/README.md'), false);
});

test('analyzeFiles sums partial service files and counts primary ctor deps', () => {
  const tenDeps = Array.from({ length: 10 }, (_, i) => `IService${i} s${i}`).join(',\n    ');
  const body = Array.from({ length: 700 }, (_, i) => `    // line ${i}`).join('\n');
  const files = [
    {
      path: 'src/Sections/Humans.Tickets/Services/Checkout/TicketCheckoutService.cs',
      content: `internal sealed partial class TicketCheckoutService(\n    ${tenDeps})\n{\n${body}\n}\n`,
    },
    {
      path: 'src/Sections/Humans.Tickets/Services/Checkout/TicketCheckoutService.Admin.cs',
      content: `internal sealed partial class TicketCheckoutService\n{\n${body}\n}\n`,
    },
  ];

  const result = analyzeFiles(files);
  assert.equal(result.metrics.partial_split_services.value, 1);
  assert.equal(result.metrics.giant_services.value, 1);
  assert.equal(result.metrics.service_ctor_deps.value, 1);
  assert.equal(result.metrics.service_ctor_deps.max, 10);
});

test('analyzeFiles measures long identifiers, positional records, tests, localizer, and rules', () => {
  const longMethodBody = Array.from({ length: 125 }, () => '        x++;').join('\n');
  const files = [
    {
      path: 'src/Sections/Humans.Demo/Services/DemoService.cs',
      content: [
        'public record HugeDto(Guid A, string B, int C, bool D, string E, string F, string G, string H, string I, string J, Dictionary<string, List<int>> K, string L);',
        'public record DemoSaveResult(bool Succeeded, string? Error);',
        'internal sealed class DemoService',
        '{',
        '    public void VeryLongMethodNameExceedingFortyFiveCharactersAsync()',
        '    {',
        '        var extremelyLongLocalVariableNameOverTwentyEight = 1;',
        '        var x = 0;',
        '        var msg = string.Format(localizer["Key"].Value, x);',
        longMethodBody,
        '    }',
        '}',
      ].join('\n'),
    },
    {
      path: 'src/Sections/Humans.Demo/Controllers/DemoController.cs',
      content: 'try { } catch (Exception ex) { _logger.LogError(ex, "fail"); }',
    },
    {
      path: 'src/Sections/Humans.Demo/Views/Index.cshtml',
      content: '<style>\n.a { color: red; }\n</style>\n<script>\nconst a = 1;\nconst b = 2;\n</script>\n',
    },
    {
      path: 'tests/Humans.Demo.Tests/DemoServiceTests.cs',
      content: [
        'public class DemoServiceTests {',
        '    public void This_test_method_name_is_far_longer_than_eighty_characters_and_reads_like_an_entire_english_sentence() {',
        '        Assert.True(true);',
        '        1.Should().Be(1);',
        '        new DbContextOptionsBuilder().UseInMemoryDatabase("x");',
        '    }',
        '}',
      ].join('\n'),
    },
    {
      path: 'memory/INDEX.md',
      content: '# Index\n- example\n',
    },
    {
      path: 'memory/code/example.md',
      content: 'HARD RULE. Do not do X.\n',
    },
    {
      path: 'AGENTS.md',
      content: '# Agents\nline 2\n',
    },
  ];

  const result = analyzeFiles(files);
  assert.equal(result.metrics.long_local_vars.value, 1);
  assert.equal(result.metrics.long_method_names.value, 1);
  assert.equal(result.metrics.long_methods.value, 1);
  assert.equal(result.metrics.big_positional_records.value, 1);
  assert.equal(result.metrics.big_positional_records.max, 12);
  assert.equal(result.metrics.bespoke_result_types.value, 1);
  assert.equal(result.metrics.localizer_string_format.value, 1);
  assert.equal(result.metrics.broad_catch_controllers.value, 1);
  assert.equal(result.metrics.long_test_names_80.value, 1);
  assert.equal(result.metrics.test_assert_mixed_files.value, 1);
  assert.equal(result.metrics.test_scaffolding_dup.value, 1);
  assert.equal(result.metrics.razor_inline_js_lines.value, 2);
  assert.equal(result.metrics.razor_inline_style_lines.value, 1);
  assert.equal(result.metrics.memory_atoms.value, 1);
  assert.equal(result.metrics.memory_hard_rules.value, 1);
});

test('compareSnapshots flags accelerating metrics and stays quiet on flat or shrinking ones', () => {
  const baseline = {
    metrics: {
      long_local_vars: { label: 'Local vars', value: 2, max: 28, outliers: [] },
      broad_catch_controllers: { label: 'Controller catches', value: 100, outliers: [] },
      test_to_src_ratio: { label: 'Ratio', value: 1.1, isRatio: true, outliers: [] },
    },
  };
  const current = {
    metrics: {
      long_local_vars: { label: 'Local vars', value: 6, max: 42, outliers: ['a.cs:1'] },
      broad_catch_controllers: { label: 'Controller catches', value: 98, outliers: [] },
      test_to_src_ratio: { label: 'Ratio', value: 1.18, isRatio: true, outliers: [] },
    },
  };

  const rows = compareSnapshots(current, baseline);
  const byId = Object.fromEntries(rows.map(r => [r.id, r]));
  assert.equal(byId.long_local_vars.flagged, true);
  assert.equal(byId.broad_catch_controllers.flagged, false);
  assert.equal(byId.test_to_src_ratio.flagged, true);

  const md = formatMarkdown({ headShort: 'abc1234', headDate: '2026-11-27', baseShort: 'def5678', baseDate: '2026-10-28', rows });
  assert.match(md, /\*\*FLAG\*\*/);
  assert.match(md, /a\.cs:1/);
});

test('long_methods brace depth ignores URL strings, char literals, and block comments', () => {
  const filler = Array.from({ length: 120 }, () => '        x++;').join('\n');
  const files = [
    {
      path: 'src/Sections/Humans.Demo/Services/DemoService.cs',
      content: [
        'internal sealed class DemoService',
        '{',
        '    public void ShortWithCharBrace(string s)',
        '    {',
        "        var parts = s.Split('{');",
        '        /* { multi-line',
        '           block comment { */',
        '        if (s.StartsWith("https://example.com")) {',
        '            return;',
        '        }',
        '    }',
        '    public void LongWithUrlAndCharBrace(string s)',
        '    {',
        '        var x = 0;',
        '        if (s.StartsWith("https://example.com")) {',
        "            if (s.Contains('}')) x++;",
        '        }',
        filler,
        '    }',
        '}',
      ].join('\n'),
    },
  ];

  const result = analyzeFiles(files);
  assert.equal(result.metrics.long_methods.value, 1);
  assert.match(result.metrics.long_methods.outliers[0], /LongWithUrlAndCharBrace/);
});

test('broad catch metrics match Exception and bare catch, not narrow exception types', () => {
  const files = [
    {
      path: 'src/Sections/Humans.Demo/Controllers/DemoController.cs',
      content: [
        'try { } catch (OperationCanceledException) { }',
        'try { } catch (IOException ex) { }',
        'try { } catch (CultureNotFoundException) { }',
        'try { } catch (Exception ex) { }',
        'try { } catch (System.Exception) { }',
        'try { } catch { }',
      ].join('\n'),
    },
  ];

  const result = analyzeFiles(files);
  assert.equal(result.metrics.broad_catch_controllers.value, 3);
  assert.equal(result.metrics.broad_catch_total.value, 3);
});

test('compareSnapshots flags max growth even when violation count is flat or lower', () => {
  const baseline = {
    metrics: {
      giant_services: { label: 'Giant services', value: 10, max: 1200, outliers: [] },
      long_methods: { label: 'Long methods', value: 12, max: 150, outliers: [] },
    },
  };
  const current = {
    metrics: {
      giant_services: { label: 'Giant services', value: 10, max: 2000, outliers: ['svc.cs'] },
      long_methods: { label: 'Long methods', value: 11, max: 210, outliers: ['m.cs:1'] },
    },
  };

  const rows = compareSnapshots(current, baseline);
  const byId = Object.fromEntries(rows.map(r => [r.id, r]));
  assert.equal(byId.giant_services.flagged, true);
  assert.equal(byId.giant_services.flagReason, 'max 1200 -> 2000');
  assert.equal(byId.long_methods.flagged, true);
  assert.equal(byId.long_methods.flagReason, 'max 150 -> 210');
});
