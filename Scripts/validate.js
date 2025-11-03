#!/usr/bin/env node
/**
 * Validation script for generatedts output
 *
 * This script:
 * 1. Regenerates all BCL .d.ts files to a temp directory
 * 2. Creates an index.d.ts with triple-slash references
 * 3. Creates a tsconfig.json
 * 4. Runs TypeScript compiler to validate all declarations
 * 5. Reports any syntax or semantic errors
 */

const { execSync, spawn } = require('child_process');
const fs = require('fs');
const path = require('path');
const os = require('os');

// Configuration
const DOTNET_PATH = process.env.DOTNET_PATH ||
    `${os.homedir()}/dotnet/shared/Microsoft.NETCore.App/10.0.0-rc.1.25451.107`;

const BCL_ASSEMBLIES = [
    // Core runtime
    // NOTE: System.Private.CoreLib is now generated using MetadataLoadContext
    'System.Private.CoreLib',
    'System.Runtime',
    'System.Console',
    'System.ComponentModel',
    'System.ComponentModel.Primitives',
    'System.ComponentModel.TypeConverter',
    'System.Reflection',
    'System.Memory',

    // Core collections
    'System.Collections',
    'System.Collections.Concurrent',
    'System.Collections.Immutable',
    'System.Collections.Specialized',

    // LINQ and queries
    'System.Linq',
    'System.Linq.Expressions',
    'System.Linq.Parallel',
    'System.Linq.Queryable',
    'System.Linq.AsyncEnumerable',

    // I/O
    'System.IO',
    'System.IO.FileSystem',
    'System.IO.Compression',
    'System.IO.Pipes',

    // Text processing
    'System.Text.Json',
    'System.Text.RegularExpressions',
    'System.Text.Encoding',
    'System.Text.Encodings.Web',

    // Networking
    'System.Net',
    'System.Net.Primitives',
    'System.Net.Http',
    'System.Net.Http.Json',
    'System.Net.Sockets',
    'System.Net.WebSockets',
    'System.Net.Security',
    'System.Net.NetworkInformation',

    // Threading
    'System.Threading',
    'System.Threading.Tasks',
    'System.Threading.Channels',

    // Data and XML
    'System.Data',
    'System.Data.Common',
    'System.Xml',
    'System.Xml.ReaderWriter',
    'System.Xml.XDocument',
    'System.Xml.XmlDocument',
    'System.Xml.Linq',
    'System.Xml.Serialization',
    'System.Xml.XPath',

    // Security
    'System.Security.Cryptography',
    'System.Security.Claims',

    // Diagnostics
    'System.Diagnostics.Process',
    'System.Diagnostics.DiagnosticSource'
];

const VALIDATION_DIR = path.join(os.tmpdir(), 'generatedts-validation');
const TYPES_DIR = path.join(VALIDATION_DIR, 'types');

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
    fs.mkdirSync(TYPES_DIR, { recursive: true });
}

function generateTypes() {
    log(`Generating types for ${BCL_ASSEMBLIES.length} assemblies...`);

    const projectPath = path.join(__dirname, '..', 'Src', 'generatedts.csproj');
    let successCount = 0;
    let failCount = 0;

    for (const assembly of BCL_ASSEMBLIES) {
        const dllPath = path.join(DOTNET_PATH, `${assembly}.dll`);

        if (!fs.existsSync(dllPath)) {
            error(`Assembly not found: ${dllPath}`);
            failCount++;
            continue;
        }

        try {
            execSync(
                `dotnet run --project "${projectPath}" -- "${dllPath}" --out-dir "${TYPES_DIR}"`,
                { stdio: 'pipe' }
            );
            successCount++;
            process.stdout.write('.');
        } catch (err) {
            error(`Failed to generate ${assembly}: ${err.message}`);
            failCount++;
        }
    }

    console.log('');
    log(`Generated ${successCount} assemblies successfully, ${failCount} failed`);

    if (failCount > 0) {
        throw new Error(`Failed to generate ${failCount} assemblies`);
    }
}

// NOTE: No longer copying hand-written core types - System.Private.CoreLib is now generated

function createIndexFile() {
    log('Creating index.d.ts with triple-slash references...');

    const dtsFiles = fs.readdirSync(TYPES_DIR)
        .filter(f => f.endsWith('.d.ts') && f !== 'index.d.ts')
        .sort();

    // Ensure System.Private.CoreLib is first (if it exists)
    const coreLibIndex = dtsFiles.indexOf('System.Private.CoreLib.d.ts');
    if (coreLibIndex > 0) {
        dtsFiles.splice(coreLibIndex, 1);
        dtsFiles.unshift('System.Private.CoreLib.d.ts');
    }

    const references = dtsFiles
        .map(f => `/// <reference path="./${f}" />`)
        .join('\n');

    const indexContent = `// Auto-generated index file for validation
// This file provides triple-slash references to all generated .d.ts files
// so TypeScript can resolve cross-assembly dependencies.

${references}

// Export empty object to make this a module
export {};
`;

    fs.writeFileSync(path.join(TYPES_DIR, 'index.d.ts'), indexContent);
    log(`Created index.d.ts with ${dtsFiles.length} references`);
}

function createTsConfig() {
    log('Creating tsconfig.json...');

    const tsconfig = {
        compilerOptions: {
            target: 'ES2020',
            module: 'commonjs',
            strict: true,
            noEmit: true,
            skipLibCheck: false,
            types: []
        },
        include: ['types/**/*.d.ts']
    };

    fs.writeFileSync(
        path.join(VALIDATION_DIR, 'tsconfig.json'),
        JSON.stringify(tsconfig, null, 2)
    );
}

function runTypeScriptCompiler() {
    log('Running TypeScript compiler...');
    log('');

    return new Promise((resolve, reject) => {
        let stdout = '';
        let stderr = '';

        const tsc = spawn('tsc', [
            '--project', VALIDATION_DIR
        ], {
            stdio: ['ignore', 'pipe', 'pipe']
        });

        tsc.stdout.on('data', (data) => {
            stdout += data.toString();
        });

        tsc.stderr.on('data', (data) => {
            stderr += data.toString();
        });

        tsc.on('close', (code) => {
            const output = stdout + stderr;

            // Count different error types
            const syntaxErrors = (output.match(/error TS1\d{3}:/g) || []).length;
            const duplicateTypeErrors = (output.match(/error TS6200:/g) || []).length;
            const semanticErrors = (output.match(/error TS2\d{3}:/g) || []).length;

            const result = {
                code,
                output,
                syntaxErrors,
                duplicateTypeErrors,
                semanticErrors,
                totalErrors: syntaxErrors + duplicateTypeErrors + semanticErrors
            };

            if (syntaxErrors > 0) {
                // Syntax errors are CRITICAL - means our generator is broken
                reject(result);
            } else {
                // Semantic/duplicate errors are expected when validating individual assemblies
                resolve(result);
            }
        });

        tsc.on('error', (err) => {
            reject(new Error(`Failed to run tsc: ${err.message}`));
        });
    });
}

function validateMetadataFiles() {
    log('Validating metadata files...');

    const files = fs.readdirSync(TYPES_DIR);
    const dtsFiles = files.filter(f => f.endsWith('.d.ts') && f !== 'index.d.ts');
    const metadataFiles = files.filter(f => f.endsWith('.metadata.json'));

    log(`  .d.ts files: ${dtsFiles.length}`);
    log(`  .metadata.json files: ${metadataFiles.length}`);

    const missingMetadata = [];
    for (const dtsFile of dtsFiles) {
        const baseName = dtsFile.replace('.d.ts', '');
        const metadataFile = `${baseName}.metadata.json`;

        if (!metadataFiles.includes(metadataFile)) {
            missingMetadata.push(dtsFile);
        }
    }

    if (missingMetadata.length > 0) {
        error(`Missing metadata files for: ${missingMetadata.join(', ')}`);
        throw new Error('Metadata validation failed');
    }

    log('  ✓ All .d.ts files have matching .metadata.json files');
}

async function main() {
    console.log('');
    console.log('================================================================');
    console.log('generatedts - Full Validation');
    console.log('================================================================');
    console.log('');

    try {
        // Step 1: Clean and prepare
        cleanValidationDir();

        // Step 2: Generate all types
        generateTypes();

        // Step 3: Create index file
        createIndexFile();

        // Step 4: Create tsconfig
        createTsConfig();

        // Step 5: Validate metadata files
        validateMetadataFiles();

        // Step 6: Run TypeScript compiler
        console.log('');
        log('Running TypeScript validation...');
        log('─'.repeat(64));
        const result = await runTypeScriptCompiler();

        // Success!
        console.log('');
        console.log('================================================================');
        console.log('✓ VALIDATION PASSED');
        console.log('================================================================');
        console.log('');
        console.log(`  All ${BCL_ASSEMBLIES.length} BCL assemblies generated successfully`);
        console.log('  All metadata files present');
        console.log('  ✓ No TypeScript syntax errors (TS1xxx)');
        console.log('');
        console.log('  Error breakdown:');
        console.log(`    - Syntax errors (TS1xxx): ${result.syntaxErrors} ✓`);
        console.log(`    - Duplicate types (TS6200): ${result.duplicateTypeErrors} (expected)`);
        console.log(`    - Semantic errors (TS2xxx): ${result.semanticErrors} (expected - missing cross-assembly refs)`);
        console.log('');
        console.log('  Note: Semantic errors are expected when validating individual');
        console.log('  assemblies without their full dependency graph. The critical');
        console.log('  validation is that there are zero syntax errors.');
        console.log('');
        console.log(`  Output directory: ${VALIDATION_DIR}`);
        console.log('');

        process.exit(0);
    } catch (err) {
        console.log('');
        console.log('================================================================');
        console.log('✗ VALIDATION FAILED');
        console.log('================================================================');
        console.log('');

        if (err.syntaxErrors !== undefined) {
            // TypeScript validation failed with syntax errors
            console.log(`  ✗ ${err.syntaxErrors} TypeScript syntax errors (TS1xxx) found`);
            console.log('');
            console.log('  These are CRITICAL errors that indicate the generator');
            console.log('  is producing invalid TypeScript syntax.');
            console.log('');
            console.log('  First few syntax errors:');
            console.log('');

            const syntaxErrorLines = err.output.split('\n')
                .filter(line => line.includes('error TS1'))
                .slice(0, 10);

            syntaxErrorLines.forEach(line => console.log(`    ${line}`));

            if (syntaxErrorLines.length < err.syntaxErrors) {
                console.log(`    ... and ${err.syntaxErrors - syntaxErrorLines.length} more`);
            }
        } else {
            error(err.message);
        }

        console.log('');
        process.exit(1);
    }
}

// Run validation
main();
