#!/usr/bin/env node

/**
 * Compares Phase 1/2 snapshots with Phase 4 TypeScript output.
 *
 * Usage:
 *   node scripts/compare-snapshots.js <validation-dir>
 *
 * Example:
 *   node scripts/compare-snapshots.js .tests/validation
 */

import fs from 'fs';
import path from 'path';

function main() {
    const args = process.argv.slice(2);

    if (args.length === 0) {
        console.error('Usage: node scripts/compare-snapshots.js <validation-dir>');
        console.error('Example: node scripts/compare-snapshots.js .tests/validation');
        process.exit(1);
    }

    const validationDir = args[0];
    const namespacesDir = path.join(validationDir, 'namespaces');

    if (!fs.existsSync(namespacesDir)) {
        console.error(`Error: Namespaces directory not found: ${namespacesDir}`);
        process.exit(1);
    }

    console.log('=== SNAPSHOT VS TYPESCRIPT OUTPUT COMPARISON ===');
    console.log();

    const namespaces = fs.readdirSync(namespacesDir, { withFileTypes: true })
        .filter(dirent => dirent.isDirectory())
        .map(dirent => dirent.name);

    let totalSnapshotTypes = 0;
    let totalGeneratedTypes = 0;
    let totalDiscrepancies = 0;
    const discrepancies = [];

    for (const ns of namespaces) {
        const nsDir = path.join(namespacesDir, ns);
        const snapshotPath = path.join(nsDir, 'snapshot.json');
        const typeListPath = path.join(nsDir, 'typelist.json');

        if (!fs.existsSync(snapshotPath)) {
            console.log(`⚠️  ${ns}: Missing snapshot.json`);
            continue;
        }

        if (!fs.existsSync(typeListPath)) {
            console.log(`⚠️  ${ns}: Missing typelist.json (run with --debug-typelist)`);
            continue;
        }

        // Read Phase 2 snapshot
        const snapshot = JSON.parse(fs.readFileSync(snapshotPath, 'utf8'));
        const snapshotTypes = snapshot.types || [];

        // Read Phase 4 type list
        const typeList = JSON.parse(fs.readFileSync(typeListPath, 'utf8'));
        const generatedTypes = typeList.types || [];

        totalSnapshotTypes += snapshotTypes.length;
        totalGeneratedTypes += generatedTypes.length;

        const diff = snapshotTypes.length - generatedTypes.length;

        if (diff !== 0) {
            totalDiscrepancies += Math.abs(diff);
            const percent = ((Math.abs(diff) / snapshotTypes.length) * 100).toFixed(1);

            discrepancies.push({
                namespace: ns,
                snapshot: snapshotTypes.length,
                generated: generatedTypes.length,
                diff: diff,
                percent: percent
            });
        }
    }

    // Display discrepancies
    if (discrepancies.length > 0) {
        console.log('Namespaces with discrepancies:');
        console.log();

        // Sort by absolute difference (largest first)
        discrepancies.sort((a, b) => Math.abs(b.diff) - Math.abs(a.diff));

        for (const d of discrepancies) {
            const sign = d.diff > 0 ? '-' : '+';
            console.log(`  ${d.namespace}:`);
            console.log(`    Snapshot: ${d.snapshot}`);
            console.log(`    Generated: ${d.generated}`);
            console.log(`    Difference: ${sign}${Math.abs(d.diff)} (${sign}${d.percent}%)`);
            console.log();
        }
    } else {
        console.log('✓ No discrepancies found!');
        console.log();
    }

    // Summary
    console.log('=== SUMMARY ===');
    console.log(`Total namespaces: ${namespaces.length}`);
    console.log(`Snapshot types: ${totalSnapshotTypes}`);
    console.log(`Generated types: ${totalGeneratedTypes}`);

    const totalDiff = totalSnapshotTypes - totalGeneratedTypes;
    if (totalDiff !== 0) {
        const sign = totalDiff > 0 ? '-' : '+';
        const percent = ((Math.abs(totalDiff) / totalSnapshotTypes) * 100).toFixed(1);
        console.log(`Difference: ${sign}${Math.abs(totalDiff)} (${sign}${percent}%)`);
    } else {
        console.log('Difference: 0 (perfect match!)');
    }
    console.log();

    if (totalDiscrepancies > 0) {
        console.log(`⚠️  Found discrepancies in ${discrepancies.length} namespace(s)`);
        process.exit(1);
    } else {
        console.log('✓ All namespaces match!');
    }
}

main();
