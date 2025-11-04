#!/usr/bin/env node
/**
 * Parse TypeScript compiler errors into structured JSON format
 * Usage: node Scripts/parse-errors.js < tsc-output.txt > errors.json
 */

import fs from 'fs';
import readline from 'readline';

async function parseErrors(input) {
    const errors = [];
    const rl = readline.createInterface({
        input: input,
        crlfDelay: Infinity
    });

    const errorPattern = /^(.+?)\((\d+),(\d+)\):\s+error\s+(TS\d+):\s+(.+)$/;

    for await (const line of rl) {
        const match = line.match(errorPattern);
        if (match) {
            const [, file, line, column, code, message] = match;
            errors.push({
                file: file.trim(),
                line: parseInt(line, 10),
                column: parseInt(column, 10),
                code: code,
                message: message.trim()
            });
        }
    }

    return errors;
}

async function main() {
    const errors = await parseErrors(process.stdin);

    // Group by error code
    const byCode = {};
    errors.forEach(err => {
        if (!byCode[err.code]) {
            byCode[err.code] = [];
        }
        byCode[err.code].push(err);
    });

    // Generate summary
    const summary = {
        timestamp: new Date().toISOString(),
        totalErrors: errors.length,
        errorCounts: Object.entries(byCode).map(([code, errs]) => ({
            code,
            count: errs.length
        })).sort((a, b) => b.count - a.count),
        errors: errors
    };

    console.log(JSON.stringify(summary, null, 2));
}

main().catch(err => {
    console.error('Error parsing:', err.message);
    process.exit(1);
});
