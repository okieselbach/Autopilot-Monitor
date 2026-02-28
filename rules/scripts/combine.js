#!/usr/bin/env node
/**
 * Combines individual rule JSON files into dist/ array files.
 * Run: node rules/scripts/combine.js
 *
 * Reads all .json files from rules/gather/, rules/analyze/, rules/ime-log-patterns/
 * and writes combined arrays to rules/dist/.
 */

const fs = require('fs');
const path = require('path');

const rulesRoot = path.resolve(__dirname, '..');

const configs = [
  {
    dir: path.join(rulesRoot, 'gather'),
    output: path.join(rulesRoot, 'dist', 'gather-rules.json'),
    schema: '../schema/gather-rule.schema.json',
    idField: 'ruleId'
  },
  {
    dir: path.join(rulesRoot, 'analyze'),
    output: path.join(rulesRoot, 'dist', 'analyze-rules.json'),
    schema: '../schema/analyze-rule.schema.json',
    idField: 'ruleId'
  },
  {
    dir: path.join(rulesRoot, 'ime-log-patterns'),
    output: path.join(rulesRoot, 'dist', 'ime-log-patterns.json'),
    schema: '../schema/ime-log-pattern.schema.json',
    idField: 'patternId'
  }
];

for (const config of configs) {
  const files = fs.readdirSync(config.dir).filter(f => f.endsWith('.json')).sort();
  const rules = [];

  for (const file of files) {
    const content = fs.readFileSync(path.join(config.dir, file), 'utf8');
    const rule = JSON.parse(content);
    // Remove $schema from individual entries (it's on the wrapper)
    delete rule['$schema'];
    rules.push(rule);
  }

  // Sort by ID for deterministic output
  rules.sort((a, b) => (a[config.idField] || '').localeCompare(b[config.idField] || ''));

  const wrapper = {
    $schema: config.schema,
    rules: rules
  };

  fs.mkdirSync(path.dirname(config.output), { recursive: true });
  fs.writeFileSync(config.output, JSON.stringify(wrapper, null, 2) + '\n', 'utf8');

  console.log(`${path.basename(config.output)}: ${rules.length} rules combined`);
}
