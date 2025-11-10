#!/usr/bin/env node
/**
 * Validation script for tsbindgen Single-Phase Architecture (NEW PIPELINE)
 *
 * This script:
 * 1. Cleans the validation-new directory
 * 2. Runs tsbindgen generate command with --use-new-pipeline flag
 * 3. Creates a tsconfig.json in the output directory
 * 4. Runs TypeScript compiler to validate all declarations
 * 5. Reports error breakdown by category
 *
 * Usage:
 *   node scripts/validate-new.js              # Full validation
 *   node scripts/validate-new.js --skip-tsc   # Skip TypeScript compilation
 *   node scripts/validate-new.js --verbose    # Enable verbose logging from tsbindgen
 */

import { execSync, spawn } from 'child_process';
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

const VALIDATION_DIR = path.join(__dirname, '..', '.tests', 'validate');
const PROJECT_ROOT = path.join(__dirname, '..');

function log(message) {
    console.log(`[validate-new] ${message}`);
}

function error(message) {
    console.error(`[validate-new] ERROR: ${message}`);
}

function cleanValidationDir() {
    log('Cleaning validation-new directory...');
    if (fs.existsSync(VALIDATION_DIR)) {
        fs.rmSync(VALIDATION_DIR, { recursive: true, force: true });
    }
    fs.mkdirSync(VALIDATION_DIR, { recursive: true });
}

function generateTypes(verbose = false) {
    return new Promise((resolve, reject) => {
        log('Generating TypeScript declarations using Single-Phase Architecture...');
        log(`  Source: ${DOTNET_RUNTIME_PATH}`);
        log(`  Output: ${VALIDATION_DIR}`);
        log(`  Pipeline: Single-Phase Architecture (--use-new-pipeline)`);
        if (verbose) {
            log(`  Verbose: enabled`);
        }
        log('');

        const projectPath = path.join(PROJECT_ROOT, 'src', 'tsbindgen', 'tsbindgen.csproj');

        // Build arguments array
        const args = [
            'run',
            '--project', projectPath,
            '--',
            'generate',
            '-d', DOTNET_RUNTIME_PATH,
            '-o', VALIDATION_DIR,
            '--use-new-pipeline'
        ];

        // Add --verbose only if requested
        if (verbose) {
            args.push('--verbose');
        }

        // Use spawn for streaming output (no buffering, no shell)
        const child = spawn('dotnet', args, {
            stdio: ['inherit', 'pipe', 'pipe'], // stdin=inherit, stdout=pipe, stderr=pipe
            shell: false // Direct execution, no shell wrapper
        });

        // Stream stdout in real-time
        child.stdout.on('data', (data) => {
            process.stdout.write(data);
        });

        // Stream stderr in real-time
        child.stderr.on('data', (data) => {
            process.stderr.write(data);
        });

        // Handle process completion
        child.on('close', (code) => {
            if (code === 0) {
                log('');
                log('✓ Type generation completed');
                log('');
                resolve();
            } else {
                error(`Type generation failed with exit code ${code}`);
                reject(new Error('Type generation failed'));
            }
        });

        // Handle process errors
        child.on('error', (err) => {
            error('Failed to spawn dotnet process');
            console.error(err);
            reject(err);
        });
    });
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
            '**/*.d.ts'
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
    const outputPath = path.join(PROJECT_ROOT, '.tests', 'tsc-validation-new.txt');
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

    // New pipeline outputs namespace folders directly to VALIDATION_DIR (no 'namespaces/' subdirectory)
    const namespacesDir = VALIDATION_DIR;

    if (!fs.existsSync(namespacesDir)) {
        throw new Error('Validation directory not found');
    }

    const namespaces = fs.readdirSync(namespacesDir);
    let missingMetadata = 0;
    let missingIndex = 0;

    for (const ns of namespaces) {
        const nsPath = path.join(namespacesDir, ns);

        // Skip non-directories (files like index.d.ts, bindings.json)
        if (!fs.statSync(nsPath).isDirectory()) continue;

        // Skip internal subdirectories (not real namespaces)
        // 'internal' was used before for root namespace, '_root' is used now to avoid case collision
        // '_support' contains marker types (TSUnsafePointer, TSByRef)
        if (ns === 'internal' || ns === '_root' || ns === '_support') continue;

        const indexPath = path.join(nsPath, 'internal', 'index.d.ts');
        const metadataPath = path.join(nsPath, 'internal', 'metadata.json');

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
    console.log('tsbindgen - Single-Phase Architecture Validation (NEW PIPELINE)');
    console.log('================================================================');
    console.log('');

    // Check for command-line flags
    const skipTsc = process.argv.includes('--skip-tsc');
    const verbose = process.argv.includes('--verbose');

    try {
        // Step 1: Clean and prepare
        cleanValidationDir();

        // Step 2: Create tsconfig (before generation so it exists even if generation fails)
        createTsConfig();

        // Step 3: Generate all types using new pipeline (streaming, no buffer)
        await generateTypes(verbose);

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
            console.log(`  Namespaces generated: check ${VALIDATION_DIR}`);
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
        console.log('VALIDATION RESULTS (Single-Phase Architecture)');
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
            console.log('  See improvement-roadmap.md for details.');
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
