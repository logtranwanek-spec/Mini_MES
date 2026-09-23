/** Server-backed B3 L2 WIP. Read helpers operate on the latest server snapshot only. */
const WIPManager = {
 cards: [], revision: -1, OVERDUE_THRESHOLD_HOURS: 72, MAX_MOS_PER_CARD: 999,
 cardsByShelf: new Map(), cardByMO: new Map(), cardById: new Map(),
calculateDuration(startDate, endDate) {
        const diff = endDate - startDate;
        const hours = Math.floor(diff / (1000 * 60 * 60));
        const minutes = Math.floor((diff % (1000 * 60 * 60)) / (1000 * 60));
        
        if (hours >= 24) {
            const days = Math.floor(hours / 24);
            const remainingHours = hours % 24;
            return `${days}d ${remainingHours}h ${minutes}m`;
        } else if (hours > 0) {
            return `${hours}h ${minutes}m`;
        } else {
            return `${minutes}m`;
        }
    },
isDuplicateMO(moNumber) {
        const upperMO = moNumber.toUpperCase().trim();
        return this.cardByMO.has(upperMO);
    },
getByMO(moNumber) {
        const upperMO = moNumber.toUpperCase().trim();
        return this.cardByMO.get(upperMO) || null;
    },
search(searchTerm) {
        const term = searchTerm.toUpperCase().trim();
        const results = [];
        
        // Extract base MO from search term (remove -XE suffix if exists)
        const baseTerm = this.getBaseMO(term);
        
        this.cards.forEach(card => {
            const moNumbers = card.moNumbers || (card.moNumber ? [card.moNumber] : []);
            const shelfMatch = card.shelfCode.toUpperCase().trim() === term;
            
            // Match by shelf code
            if (shelfMatch) {
                results.push(card);
                return;
            }
            
            // Match by exact MO
            const exactMatch = moNumbers.some(mo => 
                mo.toUpperCase().trim() === term
            );
            
            if (exactMatch) {
                results.push(card);
                return;
            }
            
            // NEW: Match by base MO (e.g., search "A123" finds "A123-XE1", "A123-XE2", etc.)
            const baseMatch = moNumbers.some(mo => {
                const moBase = this.getBaseMO(mo.toUpperCase().trim());
                return moBase === baseTerm;
            });
            
            if (baseMatch) {
                results.push(card);
                return;
            }
        });
        
        return results;
    },
getByLine(line) {
        const result = [];
        for (const [shelf, cards] of this.cardsByShelf) if (shelf.startsWith(line + '-')) result.push(...cards);
        return result;
    },
getByShelf(shelfCode) {
        return this.cardsByShelf.get(shelfCode) || [];
    },
getCount() {
        return this.cards.length;
    },
getCountByLine() {
        const counts = {};
        const lines = ShelfLocations.getAllLines();
        
        lines.forEach(line => {
            counts[line] = this.cards.filter(card => card.shelfCode.startsWith(line + '-')).length;
        });
        
        return counts;
    },
getAll() {
        return this.cards;
    },
getOverdue(threshold = this.OVERDUE_THRESHOLD_HOURS) {
        const now = new Date();
        return this.cards
            .filter(card => {
                const createdDate = card.createdAt ? new Date(card.createdAt) : new Date();
                const hoursDiff = (now - createdDate) / (1000 * 60 * 60);
                return hoursDiff >= threshold;
            })
            .map(card => {
                const createdDate = card.createdAt ? new Date(card.createdAt) : new Date();
                const hoursDiff = (now - createdDate) / (1000 * 60 * 60);

                return {
                    ...card,
                    hoursDiff: Math.floor(hoursDiff * 10) / 10,
                    tier: 'alert',
                    color: 'oklch(0.637 0.237 25.331)',
                    icon: 'solar:danger-triangle-bold'
                };
            })
            .sort((a, b) => b.hoursDiff - a.hoursDiff);
    },
getOverdueByTier() {
        const now = new Date();
        const result = {
            alert: [], // 3+ days (Red)
            total: 0
        };

        this.cards.forEach(card => {
            const createdDate = card.createdAt ? new Date(card.createdAt) : new Date();
            const hoursDiff = (now - createdDate) / (1000 * 60 * 60);

            if (hoursDiff >= this.OVERDUE_THRESHOLD_HOURS) {
                result.alert.push({
                    ...card,
                    hoursDiff: Math.floor(hoursDiff * 10) / 10,
                    tier: 'alert',
                    color: 'oklch(0.637 0.237 25.331)',
                    icon: 'solar:danger-triangle-bold'
                });
            }
        });

        result.alert.sort((a, b) => b.hoursDiff - a.hoursDiff);
        result.total = result.alert.length;

        return result;
    },
getStats() {
        const now = new Date();
        const lineCounts = this.getCountByLine();
        const overdueByTier = this.getOverdueByTier();
        
        // Calculate average time in storage
        let totalHours = 0;
        this.cards.forEach(card => {
            const createdDate = card.createdAt ? new Date(card.createdAt) : new Date();
            const hours = (now - createdDate) / (1000 * 60 * 60);
            totalHours += hours;
        });
        
        const avgHours = this.cards.length > 0 ? totalHours / this.cards.length : 0;
        
        return {
            total: this.cards.length,
            byLine: lineCounts,
            overdue: {
                total: overdueByTier.total,
                alertCards: overdueByTier.alert
            },
            averageHoursInStorage: Math.round(avgHours * 10) / 10,
            availableSlots: Object.keys(ShelfLocations.locations).length - this.cards.length,
            capacityUsed: Math.round((this.cards.length / Object.keys(ShelfLocations.locations).length) * 100 * 10) / 10
        };
    },
getByTier(tier) {
        const overdueByTier = this.getOverdueByTier();
        return overdueByTier[tier] || [];
    },
getCardOverdueStatus(cardId) {
        const card = this.cards.find(c => c.id === cardId);
        if (!card) return null;

        const now = new Date();
        const createdDate = card.createdAt ? new Date(card.createdAt) : new Date();
        const hoursDiff = (now - createdDate) / (1000 * 60 * 60);
        const isOverdue = hoursDiff >= this.OVERDUE_THRESHOLD_HOURS;

        return {
            isOverdue,
            tier: isOverdue ? 'alert' : 'normal',
            hoursDiff: Math.floor(hoursDiff * 10) / 10,
            color: isOverdue ? 'oklch(0.637 0.237 25.331)' : 'gray',
            icon: isOverdue ? 'solar:danger-triangle-bold' : 'solar:box-linear'
        };
    },
isDuplicate(shelfCode, moNumber) {
        return this.isDuplicateMO(moNumber);
    },
getOldest(limit = 10) {
        return [...this.cards]
            .sort((a, b) => {
                const dateA = new Date(a.createdAt || 0);
                const dateB = new Date(b.createdAt || 0);
                return dateA - dateB;
            })
            .slice(0, limit);
    },
getNewest(limit = 10) {
        return [...this.cards]
            .sort((a, b) => {
                const dateA = new Date(a.createdAt || 0);
                const dateB = new Date(b.createdAt || 0);
                return dateB - dateA;
            })
            .slice(0, limit);
    },
getSummary() {
        const stats = this.getStats();

        return {
            timestamp: new Date().toISOString(),
            totalCards: stats.total,
            availableSlots: stats.availableSlots,
            capacityUsed: stats.capacityUsed + '%',
            averageStorageTime: stats.averageHoursInStorage + 'h',
            overdue: {
                total: stats.overdue.total
            },
            byLine: stats.byLine,
            oldestCard: this.getOldest(1)[0] || null,
            newestCard: this.getNewest(1)[0] || null
        };
    },
exportToJSON() {
        const exportData = {
            exportDate: new Date().toISOString(),
            totalCards: this.cards.length,
            cards: this.cards.map(card => {
                const overdueStatus = this.getCardOverdueStatus(card.id);
                return {
                    ...card,
                    overdueStatus
                };
            }),
            summary: this.getSummary()
        };
        
        return JSON.stringify(exportData, null, 2);
    },
getBaseMO(moNumber) {
        return moNumber.replace(/-XE\d+$/i, '');
    },
getVehiclesByBaseMO(baseMO) {
        const normalized = baseMO.trim().toUpperCase();
        
        return this.cards.filter(card => {
            const moNumbers = card.moNumbers || [card.moNumber];
            return moNumbers.some(mo => this.getBaseMO(mo).toUpperCase() === normalized);
        }).sort((a, b) => {
            const aVehicle = a.vehicleInfo?.vehicleNumber || 0;
            const bVehicle = b.vehicleInfo?.vehicleNumber || 0;
            return aVehicle - bVehicle;
        });
    }
};
