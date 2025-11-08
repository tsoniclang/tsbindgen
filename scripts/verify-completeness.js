#!/usr/bin/env node

/**
 * Completeness Verification Script
 *
 * Verifies that everything reflected from assemblies appears in the final TypeScript output.
 * Compares snapshot.json (what was reflected) against typelist.json (what was emitted).
 *
 * Approach:
 * - typelist.json = source of truth (what actually got emitted by render pipeline)
 * - snapshot.json = what was reflected from assemblies
 * - Report anything in snapshot that's missing from typelist (genuine data loss)
 */

import fs from 'fs';
import path from 'path';
import { fileURLToPath } from 'url';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);

// ANSI colors
const colors = {
    reset: '\x1b[0m',
    bright: '\x1b[1m',
    red: '\x1b[31m',
    green: '\x1b[32m',
    yellow: '\x1b[33m',
    cyan: '\x1b[36m',
};

const VALIDATION_DIR = path.join(__dirname, '../.tests/validation');
const NAMESPACES_DIR = path.join(VALIDATION_DIR, 'namespaces');

// Statistics tracking
const stats = {
    namespacesChecked: 0,
    typesInSnapshot: 0,
    typesInTypelist: 0,
    typesLost: [],
    membersInSnapshot: 0,
    membersInTypelist: 0,
    membersLost: [],
    intentionalOmissions: {
        indexers: 0,
        genericStaticMembers: 0,
        compilerGenerated: 0,
        other: 0
    },
    warnings: [],
    errors: []
};

function log(message, color = colors.reset) {
    console.log(`${color}${message}${colors.reset}`);
}

function logSection(title) {
    log('\n' + '='.repeat(70), colors.cyan);
    log(title, colors.bright + colors.cyan);
    log('='.repeat(70), colors.cyan);
}

function logError(message) {
    stats.errors.push(message);
    log(`  ✗ ${message}`, colors.red);
}

function logWarning(message) {
    stats.warnings.push(message);
    log(`  ⚠ ${message}`, colors.yellow);
}

function logSuccess(message) {
    log(`  ✓ ${message}`, colors.green);
}

function logInfo(message) {
    log(`  ${message}`, colors.reset);
}

/**
 * Load snapshot.json (what was reflected from assemblies)
 */
function loadSnapshot(namespacePath) {
    const snapshotPath = path.join(namespacePath, 'snapshot.json');
    if (!fs.existsSync(snapshotPath)) {
        return null;
    }

    try {
        const content = fs.readFileSync(snapshotPath, 'utf-8');
        return JSON.parse(content);
    } catch (err) {
        logWarning(`Failed to load snapshot: ${err.message}`);
        return null;
    }
}

/**
 * Load typelist.json (what was actually emitted to TypeScript)
 */
function loadTypelist(namespacePath) {
    const typelistPath = path.join(namespacePath, 'typelist.json');
    if (!fs.existsSync(typelistPath)) {
        return null;
    }

    try {
        const content = fs.readFileSync(typelistPath, 'utf-8');
        return JSON.parse(content);
    } catch (err) {
        logWarning(`Failed to load typelist: ${err.message}`);
        return null;
    }
}

/**
 * Normalize type names - handle case variations and backtick/underscore conversion
 */
function normalizeTypeName(name) {
    if (!name) return '';
    // Convert backtick to underscore (CLR format -> TS format)
    return name.replace(/`/g, '_');
}

/**
 * Check if a member is an intentional omission (indexers, compiler-generated, etc.)
 */
function isIntentionalOmission(memberName, memberKind) {
    // Indexers - comprehensive patterns
    if (/^(Item|get_Item|set_Item)(`\d+)?$/.test(memberName)) {
        stats.intentionalOmissions.indexers++;
        return 'indexer';
    }

    // Generic static members (TypeScript limitation)
    if (memberKind === 'Method' && /^(Default|Empty|Zero)/.test(memberName)) {
        stats.intentionalOmissions.genericStaticMembers++;
        return 'generic_static_member';
    }

    // Compiler-generated members
    if (memberName.startsWith('<') || memberName.includes('__')) {
        stats.intentionalOmissions.compilerGenerated++;
        return 'compiler_generated';
    }

    return null;
}

/**
 * Build lookup map from typelist for fast checking
 * Uses tsEmitName as the key (matches snapshot.json structure)
 */
function buildTypelistLookup(typelist) {
    const typeMap = new Map();

    if (!typelist || !typelist.types) {
        return typeMap;
    }

    for (const type of typelist.types) {
        const tsEmitName = type.tsEmitName || type.TsEmitName;
        const normalizedName = normalizeTypeName(tsEmitName);

        // Build member lookup for this type
        const memberSet = new Set();
        if (type.members) {
            for (const member of type.members) {
                // Store member with static/instance distinction
                const memberKey = `${member.isStatic ? 'static:' : 'instance:'}${member.name}`;
                memberSet.add(memberKey);
            }
        }

        typeMap.set(normalizedName, {
            kind: type.kind,
            members: memberSet
        });
    }

    return typeMap;
}

/**
 * Get types from snapshot (handle case variations)
 */
function getSnapshotTypes(snapshot) {
    return snapshot.types || snapshot.Types || [];
}

/**
 * Get members from snapshot type (handle case variations)
 */
function getSnapshotMembers(type) {
    const members = type.members || type.Members;
    if (!members) return [];

    const methods = members.methods || members.Methods || [];
    const properties = members.properties || members.Properties || [];
    const fields = members.fields || members.Fields || [];
    const events = members.events || members.Events || [];

    return [...methods, ...properties, ...fields, ...events];
}

/**
 * Get member name from snapshot member
 */
function getSnapshotMemberName(member) {
    return member.tsName || member.TsName || member.clrName || member.ClrName || '';
}

/**
 * Get member kind from snapshot member
 */
function getSnapshotMemberKind(member) {
    // Infer kind from which collection it came from
    // This is a heuristic - we'd need to track this when collecting members
    return 'Unknown';
}

/**
 * Verify namespace completeness
 */
function verifyNamespace(namespaceName, namespacePath) {
    logSection(`Verifying: ${namespaceName}`);
    stats.namespacesChecked++;

    const snapshot = loadSnapshot(namespacePath);
    const typelist = loadTypelist(namespacePath);

    if (!snapshot) {
        logWarning('No snapshot.json found');
        return;
    }

    if (!typelist) {
        logWarning('No typelist.json found');
        return;
    }

    // Build lookup from typelist (what was actually emitted)
    const typelistLookup = buildTypelistLookup(typelist);

    // Get types from snapshot (what was reflected)
    const snapshotTypes = getSnapshotTypes(snapshot);

    logInfo(`Snapshot has ${snapshotTypes.length} types`);
    logInfo(`Typelist has ${typelist.types?.length || 0} types`);

    let typesLostCount = 0;
    let membersLostCount = 0;

    // Check each type from snapshot
    for (const snapshotType of snapshotTypes) {
        const tsEmitName = snapshotType.tsEmitName || snapshotType.TsEmitName;
        const normalizedTypeName = normalizeTypeName(tsEmitName);

        stats.typesInSnapshot++;

        // Check if type exists in typelist
        if (!typelistLookup.has(normalizedTypeName)) {
            typesLostCount++;
            const kind = snapshotType.kind || snapshotType.Kind || 'Unknown';
            stats.typesLost.push({
                namespace: namespaceName,
                typeName: normalizedTypeName,
                clrName: snapshotType.clrName || snapshotType.ClrName,
                kind
            });
            logError(`Type lost: ${normalizedTypeName} (kind: ${kind})`);
            continue; // Skip member checking if type is lost
        }

        // Type exists - check members
        const typeInfo = typelistLookup.get(normalizedTypeName);
        const snapshotMembers = getSnapshotMembers(snapshotType);

        for (const snapshotMember of snapshotMembers) {
            const memberName = getSnapshotMemberName(snapshotMember);
            const memberKind = getSnapshotMemberKind(snapshotMember);
            const isStatic = snapshotMember.isStatic || snapshotMember.IsStatic || false;

            stats.membersInSnapshot++;

            // Check if intentionally omitted
            const omissionReason = isIntentionalOmission(memberName, memberKind);
            if (omissionReason) {
                continue; // Intentionally omitted, not an error
            }

            // Check if member exists in typelist
            const memberKey = `${isStatic ? 'static:' : 'instance:'}${memberName}`;
            if (!typeInfo.members.has(memberKey)) {
                membersLostCount++;
                stats.membersLost.push({
                    namespace: namespaceName,
                    type: normalizedTypeName,
                    member: memberName,
                    isStatic,
                    kind: memberKind
                });
                logError(`Member lost: ${normalizedTypeName}.${memberName} (static: ${isStatic})`);
            }
        }
    }

    // Count members in typelist
    for (const [_, typeInfo] of typelistLookup) {
        stats.membersInTypelist += typeInfo.members.size;
    }

    stats.typesInTypelist += typelistLookup.size;

    if (typesLostCount === 0 && membersLostCount === 0) {
        logSuccess(`All ${snapshotTypes.length} types and their members accounted for`);
    } else {
        if (typesLostCount > 0) {
            logError(`${typesLostCount} types lost`);
        }
        if (membersLostCount > 0) {
            logError(`${membersLostCount} members lost`);
        }
    }
}

/**
 * Generate final report
 */
function generateReport() {
    logSection('COMPLETENESS VERIFICATION REPORT');

    log(`\nNamespaces checked: ${stats.namespacesChecked}`, colors.bright);
    log(`Types in snapshots: ${stats.typesInSnapshot}`, colors.bright);
    log(`Types in typelists: ${stats.typesInTypelist}`, colors.bright);
    log(`Members in snapshots: ${stats.membersInSnapshot}`, colors.bright);
    log(`Members in typelists: ${stats.membersInTypelist}`, colors.bright);

    log('\nIntentional Omissions:', colors.cyan);
    log(`  Indexers: ${stats.intentionalOmissions.indexers}`);
    log(`  Generic static members: ${stats.intentionalOmissions.genericStaticMembers}`);
    log(`  Compiler-generated: ${stats.intentionalOmissions.compilerGenerated}`);
    log(`  Other: ${stats.intentionalOmissions.other}`);

    if (stats.typesLost.length > 0 || stats.membersLost.length > 0) {
        log('\n' + '✗'.repeat(70), colors.red);
        log('COMPLETENESS ISSUES DETECTED', colors.bright + colors.red);
        log('✗'.repeat(70), colors.red);

        if (stats.typesLost.length > 0) {
            log(`\n${stats.typesLost.length} types lost:`, colors.red);
            const sampleSize = Math.min(10, stats.typesLost.length);
            for (let i = 0; i < sampleSize; i++) {
                const t = stats.typesLost[i];
                log(`  ${t.namespace}.${t.typeName} (${t.kind})`, colors.red);
            }
            if (stats.typesLost.length > sampleSize) {
                log(`  ... and ${stats.typesLost.length - sampleSize} more`, colors.red);
            }
        }

        if (stats.membersLost.length > 0) {
            log(`\n${stats.membersLost.length} members lost:`, colors.red);
            const sampleSize = Math.min(10, stats.membersLost.length);
            for (let i = 0; i < sampleSize; i++) {
                const m = stats.membersLost[i];
                log(`  ${m.namespace}.${m.type}.${m.member} (static: ${m.isStatic})`, colors.red);
            }
            if (stats.membersLost.length > sampleSize) {
                log(`  ... and ${stats.membersLost.length - sampleSize} more`, colors.red);
            }
        }

        log(`\n✗ ${stats.errors.length} errors (see above)`, colors.red);
        log('');

        return false; // Verification failed
    } else {
        log('\n' + '✓'.repeat(70), colors.green);
        log('VERIFICATION PASSED - ALL REFLECTED DATA ACCOUNTED FOR', colors.bright + colors.green);
        log('✓'.repeat(70), colors.green);
        log('');

        return true; // Verification passed
    }
}

/**
 * Main verification
 */
function main() {
    logSection('TSBINDGEN COMPLETENESS VERIFICATION');
    log(`Validation directory: ${VALIDATION_DIR}`, colors.cyan);

    if (!fs.existsSync(NAMESPACES_DIR)) {
        log(`Error: Namespaces directory not found: ${NAMESPACES_DIR}`, colors.red);
        process.exit(1);
    }

    const namespaces = fs.readdirSync(NAMESPACES_DIR)
        .filter(name => {
            const nsPath = path.join(NAMESPACES_DIR, name);
            return fs.statSync(nsPath).isDirectory();
        })
        .sort();

    log(`Found ${namespaces.length} namespaces to verify\n`, colors.cyan);

    // Verify each namespace
    for (const namespace of namespaces) {
        const namespacePath = path.join(NAMESPACES_DIR, namespace);
        verifyNamespace(namespace, namespacePath);
    }

    // Generate final report
    const passed = generateReport();

    process.exit(passed ? 0 : 1);
}

main();
