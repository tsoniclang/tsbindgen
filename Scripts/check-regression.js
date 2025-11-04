#!/usr/bin/env node
/**
 * Regression check script
 * Compares current validation against baseline to detect regressions
 * Usage: node Scripts/check-regression.js
 */

import fs from 'fs';
import path from 'path';
import { fileURLToPath } from 'url';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);

function loadJSON(filePath) {
    if (!fs.existsSync(filePath)) {
        return null;
    }
    return JSON.parse(fs.readFileSync(filePath, 'utf8'));
}

function main() {
    const baselinePath = path.join(__dirname, '..', '.analysis', 'baseline-errors.json');
    const currentStatsPath = path.join(__dirname, '..', '.analysis', 'validation-stats.json');

    const baseline = loadJSON(baselinePath);
    const currentStats = loadJSON(currentStatsPath);

    if (!baseline) {
        console.error('❌ Baseline not found. Run validation first to establish baseline.');
        process.exit(1);
    }

    if (!currentStats) {
        console.error('❌ Current stats not found. Run validation first.');
        process.exit(1);
    }

    console.log('');
    console.log('='.repeat(64));
    console.log('Regression Check');
    console.log('='.repeat(64));
    console.log('');

    // Compare error counts
    const baselineTotal = baseline.totalErrors;
    const currentTotal = currentStats.errors.total;
    const delta = currentTotal - baselineTotal;

    console.log(`Baseline errors:  ${baselineTotal}`);
    console.log(`Current errors:   ${currentTotal}`);
    console.log(`Delta:            ${delta >= 0 ? '+' : ''}${delta}`);
    console.log('');

    // Breakdown by error code
    console.log('Error breakdown:');
    console.log('');
    console.log('Code     | Baseline | Current | Delta');
    console.log('---------+----------+---------+-------');

    const baselineCodes = new Map(baseline.errorCounts.map(e => [e.code, e.count]));
    const currentCodes = new Map();

    // Parse current error counts from semantic + duplicate
    // Note: validate.js doesn't break down by code, so we need to re-parse
    // For now, we'll just check total counts

    if (delta > 0) {
        console.log('');
        console.log(`⚠️  REGRESSION DETECTED: ${delta} more errors than baseline`);
        console.log('');
        process.exit(1);
    } else if (delta < 0) {
        console.log('');
        console.log(`✅ IMPROVEMENT: ${Math.abs(delta)} fewer errors than baseline`);
        console.log('');
        process.exit(0);
    } else {
        console.log('');
        console.log('✅ No regression (error count unchanged)');
        console.log('');
        process.exit(0);
    }
}

main();
