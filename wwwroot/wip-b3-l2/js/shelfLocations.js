/**
 * Shelf Locations Database
 * Format: X-XX where X = Line (A-P), XX = Position (01-XX)
 * Line capacities:
 *  - All lines except E, I and K: 76 positions
 *  - E: 74 positions
 *  - I: 91 positions (3 tiers of 25 + separate row of 16)
 *  - K: 90 positions (3 tiers of 30)
 */

const ShelfLocations = {
    // All valid locations stored here
    locations: {},

    // Central capacities map (dùng chung cho toàn file)
    lineCapacities: {
        'A': 76, 'B': 76, 'C': 76, 'D': 76,
        'E': 74,
        'F': 76,
        'G': 76,
        'H': 76, 'I': 91, 'J': 76, 'K': 90, 'L': 76,
        'M': 76, 'N': 76,
        'O': 76,
        'P': 76
    },

    // Chỉ các line này được phép sử dụng (thêm mới + hiển thị Dashboard)
    usableLines: 'ABCDEFGHIJKLMNOP'.split(''),
    // Automatic kit placement stays in the existing area pending product allocation.
    autoAssignLines: ['C', 'D', 'E', 'F', 'G'],

    /**
     * Initialize locations on load
     * Generates all valid shelf codes based on lineCapacities
     */
    init() {
        this.locations = {};

        Object.keys(this.lineCapacities).forEach(line => {
            if (line === 'I') {
                // Hàng 1 tầng: I1-1 → I1-16
                for (let n = 1; n <= 16; n++) {
                    this.locations[`I1-${n}`] = `I1 - Position ${n}`;
                }
                // Kệ 3 tầng
                for (let pos = 1; pos <= 25; pos++) {
                    this.locations[`I2-1.${pos}`] = `I2 Tier1 - Position ${pos}`;
                    this.locations[`I2-2.${pos}`] = `I2 Tier2 - Position ${pos}`;
                    this.locations[`I2-3.${pos}`] = `I2 Tier3 - Position ${pos}`;
                }
                return;
            }

            if (line === 'K') {
                for (let tier = 1; tier <= 3; tier++) {
                    for (let pos = 1; pos <= 30; pos++) {
                        this.locations[`K-${tier}.${pos}`] = `K Tier${tier} - Position ${pos}`;
                    }
                }
                return;
            }

            const maxPos = this.lineCapacities[line];
            for (let pos = 1; pos <= maxPos; pos++) {
                const code = `${line}-${pos.toString().padStart(2, '0')}`;
                this.locations[code] = `Line ${line} - Position ${pos}`;
            }
        });

        console.log(`✓ Generated ${Object.keys(this.locations).length} shelf locations`);
        console.log('  Line capacities:', this.lineCapacities);
    },

    /**
     * Validate if a shelf code exists
     */
    isValid(code) {
        if (!code) return false;
        const key = String(code).trim();
        if (this.locations[key]) return true;
        // chấp nhận cả viết hoa
        const upper = key.toUpperCase();
        return Object.keys(this.locations).some(k => k.toUpperCase() === upper);
    },

    /**
     * Get full location name from code
     */
    getName(code) {
        if (!this.isValid(code)) return code;
        
        const parts = code.split('-');
        const line = parts[0];
        const position = parseInt(parts[1], 10);
        
        return `Line ${line} - Position ${position}`;
    },

    /**
     * Get color based on line (A-P) - 16 distinct colors
     */
    getAreaColor(code) {
        if (!code || String(code).length < 1) return '#6B7280';

        let line = String(code).trim().toUpperCase();
        if (line.startsWith('I1') || line.startsWith('I2') || line.startsWith('I-')) {
            line = 'I';
        } else if (line.startsWith('K')) {
            line = 'K';
        } else {
            line = line.charAt(0);
        }

        const colorMap = {
            'A': '#EF4444',
            'B': '#F97316',
            'C': '#F59E0B',
            'D': '#EAB308',
            'E': '#84CC16',
            'F': '#22C55E',
            'G': '#10B981',
            'H': '#14B8A6',
            'I': '#06B6D4',
            'J': '#0EA5E9',
            'K': '#3B82F6',
            'L': '#6366F1',
            'M': '#8B5CF6',
            'N': '#A855F7',
            'O': '#D946EF',
            'P': '#EC4899'
        };

        return colorMap[line] || '#6B7280';
    },
    getAreaColorRGB(code) {
        const hex = this.getAreaColor(code);
        const result = /^#?([a-f\d]{2})([a-f\d]{2})([a-f\d]{2})$/i.exec(hex);
        
        return result ? {
            r: parseInt(result[1], 16),
            g: parseInt(result[2], 16),
            b: parseInt(result[3], 16)
        } : { r: 107, g: 114, b: 128 };
    },

    getLineName(code) {
        if (!code || code.length < 1) return 'Unknown';
        const line = code.charAt(0).toUpperCase();
        return `Line ${line}`;
    },

    getPosition(code) {
        if (!this.isValid(code)) return 0;
        const parts = code.split('-');
        return parseInt(parts[1], 10);
    },

    getLine(code) {
        if (!code) return '';
        const s = String(code).trim().toUpperCase();
        if (s.startsWith('I1') || s.startsWith('I2') || s.startsWith('I-')) return 'I';
        if (s.startsWith('K')) return 'K';
        return s.charAt(0);
    },

    /**
     * Get all locations for a specific line (with correct capacity)
     */
    getLineLocations(line) {
        if (!line) return [];
        const lineLetter = String(line).toUpperCase();
        if (!/^[A-P]$/.test(lineLetter)) return [];

        const locations = [];

        if (lineLetter === 'I') {
            for (let n = 1; n <= 16; n++) {
                const code = `I1-${n}`;
                locations.push({
                    code,
                    name: this.locations[code] || code,
                    color: this.getAreaColor(code),
                    line: 'I',
                    position: n
                });
            }
            for (const tierCode of ['I2-1', 'I2-2', 'I2-3']) {
                for (let pos = 1; pos <= 25; pos++) {
                    const code = `${tierCode}.${pos}`;
                    locations.push({
                        code,
                        name: this.locations[code] || code,
                        color: this.getAreaColor(code),
                        line: 'I',
                        position: pos
                    });
                }
            }
            return locations;
        }

        if (lineLetter === 'K') {
            for (let tier = 1; tier <= 3; tier++) {
                for (let pos = 1; pos <= 30; pos++) {
                    const code = `K-${tier}.${pos}`;
                    locations.push({
                        code,
                        name: this.locations[code] || code,
                        color: this.getAreaColor(code),
                        line: 'K',
                        position: pos
                    });
                }
            }
            return locations;
        }

        const maxPos = this.lineCapacities[lineLetter] || 70;
        for (let pos = 1; pos <= maxPos; pos++) {
            const code = `${lineLetter}-${pos.toString().padStart(2, '0')}`;
            locations.push({
                code,
                name: this.locations[code] || code,
                color: this.getAreaColor(code),
                line: lineLetter,
                position: pos
            });
        }
        return locations;
    },

    getAllGrouped() {
        const grouped = {};
        const lines = this.getAllLines();
        
        lines.forEach(line => {
            grouped[line] = this.getLineLocations(line);
        });
        
        return grouped;
    },

    getAllLines() {
        return ['A', 'B', 'C', 'D', 'E', 'F', 'G', 'H', 'I', 'J', 'K', 'L', 'M', 'N', 'O', 'P'];
    },

    /**
     * Get info for all lines with correct capacities
     */
    getAllLinesInfo() {
        return this.getAllLines().map(line => ({
            line,
            name: `Line ${line}`,
            color: this.getAreaColor(`${line}-01`),
            totalPositions: this.lineCapacities[line] || 70
        }));
    },

    /**
     * Search shelf locations (with correct capacities)
     */
    search(searchTerm) {
        if (!searchTerm) return [];
        const term = String(searchTerm).trim().toUpperCase();
        const results = [];

        Object.keys(this.locations).forEach(code => {
            const name = (this.locations[code] || '').toUpperCase();
            if (code.toUpperCase().includes(term) || name.includes(term) || this.getLine(code) === term) {
                results.push({
                    code,
                    name: this.locations[code],
                    color: this.getAreaColor(code),
                    line: this.getLine(code),
                    position: code
                });
            }
        });

        return results;
    },

    /**
     * Get a random shelf code (respecting capacities)
     */
    getRandom(line = null) {
        if (line) {
            const lineLetter = line.toUpperCase();
            if (!/^[A-P]$/.test(lineLetter)) return null;
            
            const maxPos = this.lineCapacities[lineLetter] || 70;
            const randomPos = Math.floor(Math.random() * maxPos) + 1;
            return `${lineLetter}-${randomPos.toString().padStart(2, '0')}`;
        }
        
        // Chỉ random trong các line được phép sử dụng
        const lines = this.usableLines;
        const randomLine = lines[Math.floor(Math.random() * lines.length)];
        const maxPos = this.lineCapacities[randomLine] || 70;
        const randomPos = Math.floor(Math.random() * maxPos) + 1;
        
        return `${randomLine}-${randomPos.toString().padStart(2, '0')}`;
    },

    /**
     * Get neighbor shelves within same line
     */
    getNeighbors(code) {
        if (!this.isValid(code)) return { previous: null, next: null };
        
        const line = this.getLine(code);
        const position = this.getPosition(code);
        
        const maxPos = this.lineCapacities[line] || 70;
        
        const neighbors = {
            previous: null,
            next: null,
            sameLine: {
                previous: null,
                next: null
            }
        };
        
        if (position > 1) {
            neighbors.sameLine.previous = `${line}-${(position - 1).toString().padStart(2, '0')}`;
            neighbors.previous = neighbors.sameLine.previous;
        }
        
        if (position < maxPos) {
            neighbors.sameLine.next = `${line}-${(position + 1).toString().padStart(2, '0')}`;
            neighbors.next = neighbors.sameLine.next;
        }
        
        return neighbors;
    },

    isSameLine(code1, code2) {
        if (!this.isValid(code1) || !this.isValid(code2)) return false;
        return this.getLine(code1) === this.getLine(code2);
    },

    getDistance(code1, code2) {
        if (!this.isSameLine(code1, code2)) return null;
        
        const pos1 = this.getPosition(code1);
        const pos2 = this.getPosition(code2);
        
        return Math.abs(pos1 - pos2);
    },

    getRange(startCode, endCode) {
        if (!this.isSameLine(startCode, endCode)) return null;
        
        const line = this.getLine(startCode);
        const startPos = this.getPosition(startCode);
        const endPos = this.getPosition(endCode);
        
        const minPos = Math.min(startPos, endPos);
        const maxPos = Math.max(startPos, endPos);
        
        const range = [];
        for (let pos = minPos; pos <= maxPos; pos++) {
            range.push(`${line}-${pos.toString().padStart(2, '0')}`);
        }
        
        return range;
    },

    /**
     * Statistics
     */
    getStats() {
        const totalSlots = Object.values(this.lineCapacities)
            .reduce((sum, count) => sum + count, 0);
        
        return {
            totalLocations: Object.keys(this.locations).length,
            totalLines: 16,
            totalSlots: totalSlots,  // 1,158
            positionsPerLine: 'Variable (38-76)',
            lines: this.getAllLinesInfo()
        };
    },

    /**
     * Format user input to standard code (e.g., "A5" → "A-05")
     */
    formatCode(input) {
        if (!input || typeof input !== 'string') return null;
        
        const cleaned = input.toUpperCase().trim();
        
        const patterns = [
            /^([A-P])(\d{1,2})$/,
            /^([A-P])-(\d{1,2})$/,
            /^([A-P])\s+(\d{1,2})$/,
        ];
        
        for (const pattern of patterns) {
            const match = cleaned.match(pattern);
            if (match) {
                const line = match[1];
                const position = parseInt(match[2], 10);
                
                const maxPos = this.lineCapacities[line] || 70;
                
                if (position >= 1 && position <= maxPos) {
                    return `${line}-${position.toString().padStart(2, '0')}`;
                }
            }
        }
        
        return null;
    },

    /**
     * Get next available shelf (respecting capacities)
     */
    getNextAvailable() {
        // Chỉ xếp hàng vào các line được phép sử dụng (C, D, E, F, G)
        const lines = this.autoAssignLines;
        const occupiedCodes = WIPManager.getAll().map(card => card.shelfCode);
        
        for (const line of lines) {
            const maxPos = this.lineCapacities[line] || 70;
            
            for (let pos = 1; pos <= maxPos; pos++) {
                const code = `${line}-${pos.toString().padStart(2, '0')}`;
                
                if (!occupiedCodes.includes(code)) {
                    return code;
                }
            }
        }
        
        return null;
    },

    /**
     * Kiểm tra line có được phép sử dụng mới không
     */
    isUsableLine(lineOrCode) {
        if (!lineOrCode) return false;
        const line = String(lineOrCode).charAt(0).toUpperCase();
        return (this.usableLines || []).includes(line);
    },
};

ShelfLocations.init();
