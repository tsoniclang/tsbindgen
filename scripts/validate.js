#!/usr/bin/env node
/**
 * Validation script for tsbindgen namespace-based output
 *
 * This script:
 * 1. Cleans the validation directory
 * 2. Runs tsbindgen generate command on the full .NET framework
 * 3. Creates a tsconfig.json in the output directory
 * 4. Runs TypeScript compiler to validate all declarations
 * 5. Reports error breakdown by category
 */

import { execSync } from 'child_process';
import fs from 'fs';
import path from 'path';
import os from 'os';
import { fileURLToPath } from 'url';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);

// Configuration
const DOTNET_VERSION = '10.0.0-rc.1.25451.107';
const DOTNET_HOME = os.homedir() + '/dotnet';
const DOTNET_RUNTIME_PATH = `${DOTNET_HOME}/shared/Microsoft.NETCore.App/${DOTNET_VERSION}`;

const VALIDATION_DIR = path.join(__dirname, '..', '.tests', 'validation');
const PROJECT_ROOT = path.join(__dirname, '..');

function log(message) {
    console.log(`[validate] ${message}`);
}

function error(message) {
    console.error(`[validate] ERROR: ${message}`);
}

function cleanValidationDir() {
    log('Cleaning validation directory...');
    if (fs.existsSync(VALIDATION_DIR)) {
        fs.rmSync(VALIDATION_DIR, { recursive: true, force: true });
    }
    fs.mkdirSync(VALIDATION_DIR, { recursive: true });
}

function generateTypes() {
    log('Generating TypeScript declarations for entire .NET framework...');
    log(`  Source: ${DOTNET_RUNTIME_PATH}`);
    log(`  Output: ${VALIDATION_DIR}`);
    log('');

    const projectPath = path.join(PROJECT_ROOT, 'src', 'tsbindgen', 'tsbindgen.csproj');

    try {
        const output = execSync(
            `dotnet run --project "${projectPath}" -- generate -d "${DOTNET_RUNTIME_PATH}" -o "${VALIDATION_DIR}" --debug-typelist`,
            {
                stdio: 'pipe',
                encoding: 'utf-8',
                maxBuffer: 10 * 1024 * 1024 // 10MB buffer
            }
        );

        console.log(output);
        log('✓ Type generation completed');
        log('');
    } catch (err) {
        error('Failed to generate types');
        console.error(err.stderr || err.stdout || err.message);
        throw new Error('Type generation failed');
    }
}

function createTsConfig() {
    log('Creating tsconfig.json...');

    const tsconfig = {
        compilerOptions: {
            target: 'ES2020',
            module: 'ES2020',
            lib: ['ES2020'],
            strict: true,
            noEmit: true,
            skipLibCheck: false,
            moduleResolution: 'bundler'
        },
        include: [
            'namespaces/**/*.d.ts'
        ]
    };

    fs.writeFileSync(
        path.join(VALIDATION_DIR, 'tsconfig.json'),
        JSON.stringify(tsconfig, null, 2)
    );

    log('✓ Created tsconfig.json');
}

function runTypeScriptCompiler() {
    log('Running TypeScript compiler...');
    log('');

    let output;
    let exitCode = 0;

    try {
        output = execSync(
            `npx tsc --noEmit --project "${VALIDATION_DIR}"`,
            {
                stdio: 'pipe',
                encoding: 'utf-8',
                maxBuffer: 50 * 1024 * 1024 // 50MB buffer for errors
            }
        );
    } catch (err) {
        exitCode = err.status || 1;
        output = err.stdout || '';
    }

    // Save full output to file
    const outputPath = path.join(PROJECT_ROOT, '.tests', 'tsc-validation.txt');
    fs.mkdirSync(path.dirname(outputPath), { recursive: true });
    fs.writeFileSync(outputPath, output);

    // Count different error types
    const errorLines = output.split('\n').filter(line => line.includes('error TS'));

    const syntaxErrors = errorLines.filter(line => /error TS1\d{3}:/.test(line)).length;
    const semanticErrors = errorLines.filter(line => /error TS2\d{3}:/.test(line)).length;
    const duplicateErrors = errorLines.filter(line => /error TS6200:/.test(line)).length;
    const totalErrors = errorLines.length;

    // Breakdown by specific error codes
    const errorCounts = {};
    errorLines.forEach(line => {
        const match = line.match(/error (TS\d+):/);
        if (match) {
            const code = match[1];
            errorCounts[code] = (errorCounts[code] || 0) + 1;
        }
    });

    // Sort by count descending
    const sortedErrors = Object.entries(errorCounts)
        .sort((a, b) => b[1] - a[1])
        .slice(0, 10);

    return {
        exitCode,
        output,
        outputPath,
        totalErrors,
        syntaxErrors,
        semanticErrors,
        duplicateErrors,
        errorCounts: sortedErrors
    };
}

function validateMetadataFiles() {
    log('Validating metadata files...');

    const namespacesDir = path.join(VALIDATION_DIR, 'namespaces');

    if (!fs.existsSync(namespacesDir)) {
        throw new Error('Namespaces directory not found');
    }

    const namespaces = fs.readdirSync(namespacesDir);
    let missingMetadata = 0;
    let missingIndex = 0;

    for (const ns of namespaces) {
        const nsPath = path.join(namespacesDir, ns);
        if (!fs.statSync(nsPath).isDirectory()) continue;

        const indexPath = path.join(nsPath, 'index.d.ts');
        const metadataPath = path.join(nsPath, 'metadata.json');

        if (!fs.existsSync(indexPath)) {
            error(`  Missing index.d.ts in ${ns}`);
            missingIndex++;
        }

        if (!fs.existsSync(metadataPath)) {
            error(`  Missing metadata.json in ${ns}`);
            missingMetadata++;
        }
    }

    if (missingIndex > 0 || missingMetadata > 0) {
        throw new Error(`Missing ${missingIndex} index files and ${missingMetadata} metadata files`);
    }

    log(`  ✓ All ${namespaces.length} namespaces have index.d.ts and metadata.json`);
}

async function main() {
    console.log('');
    console.log('================================================================');
    console.log('tsbindgen - Full Framework Validation');
    console.log('================================================================');
    console.log('');

    // Check for --skip-tsc flag
    const skipTsc = process.argv.includes('--skip-tsc');

    try {
        // Step 1: Clean and prepare
        cleanValidationDir();

        // Step 2: Generate all types
        generateTypes();

        // Step 3: Create tsconfig
        createTsConfig();

        // Step 4: Validate metadata files
        validateMetadataFiles();

        // Step 5: Run TypeScript compiler (optional)
        if (skipTsc) {
            console.log('');
            console.log('================================================================');
            console.log('GENERATION COMPLETE (TypeScript validation skipped)');
            console.log('================================================================');
            console.log('');
            console.log(`  Validation directory: ${VALIDATION_DIR}`);
            console.log(`  Namespaces generated: check ${path.join(VALIDATION_DIR, 'namespaces')}`);
            console.log('');
            console.log('  Run without --skip-tsc to validate TypeScript compilation');
            console.log('');
            process.exit(0);
        }

        console.log('');
        const result = runTypeScriptCompiler();

        // Print results
        console.log('');
        console.log('================================================================');
        console.log('VALIDATION RESULTS');
        console.log('================================================================');
        console.log('');
        console.log(`  Total errors: ${result.totalErrors}`);
        console.log('');
        console.log('  Error breakdown:');
        console.log(`    - Syntax errors (TS1xxx):     ${result.syntaxErrors}`);
        console.log(`    - Semantic errors (TS2xxx):   ${result.semanticErrors}`);
        console.log(`    - Duplicate types (TS6200):   ${result.duplicateErrors}`);
        console.log('');
        console.log('  Top 10 error codes:');
        result.errorCounts.forEach(([code, count]) => {
            const pct = ((count / result.totalErrors) * 100).toFixed(1);
            console.log(`    ${count.toString().padStart(5)} ${code} (${pct}%)`);
        });
        console.log('');
        console.log(`  Full output saved to: ${result.outputPath}`);
        console.log(`  Validation directory: ${VALIDATION_DIR}`);
        console.log('');

        // Success criteria: zero syntax errors
        if (result.syntaxErrors === 0) {
            console.log('  ✓ VALIDATION PASSED - No TypeScript syntax errors');
            console.log('');
            console.log('  Note: Semantic errors are expected and documented.');
            console.log('  See CLAUDE.md for details on known limitations.');
            console.log('');
            process.exit(0);
        } else {
            console.log(`  ✗ VALIDATION FAILED - ${result.syntaxErrors} syntax errors found`);
            console.log('');
            console.log('  First 10 syntax errors:');
            const syntaxLines = result.output.split('\n')
                .filter(line => /error TS1\d{3}:/.test(line))
                .slice(0, 10);
            syntaxLines.forEach(line => console.log(`    ${line}`));
            console.log('');
            process.exit(1);
        }

    } catch (err) {
        console.log('');
        console.log('================================================================');
        console.log('✗ VALIDATION FAILED');
        console.log('================================================================');
        console.log('');
        error(err.message);
        if (err.stack) {
            console.error(err.stack);
        }
        console.log('');
        process.exit(1);
    }
}

// Run validation
main();
