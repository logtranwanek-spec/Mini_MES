/**
 * Excel Parser Module - Parse Daily Delivery Schedule
 */

const ExcelParser = {
    scheduledMOs: null,
    xlsxLoading: null,

    async ensureXlsx() {
        if (window.XLSX) return;
        if (!this.xlsxLoading) {
            this.xlsxLoading = new Promise((resolve, reject) => {
                const script = document.createElement('script');
                script.src = 'js/xlsx.full.min.js';
                script.onload = resolve;
                script.onerror = () => reject(new Error('Không tải được thư viện đọc Excel.'));
                document.head.appendChild(script);
            });
        }
        await this.xlsxLoading;
    },
    
    /**
     * Parse uploaded Excel file
     */
    async parseFile(file) {
        await this.ensureXlsx();
        return new Promise((resolve, reject) => {
            const reader = new FileReader();
            
            reader.onload = (e) => {
                try {
                    const data = new Uint8Array(e.target.result);
                    const workbook = XLSX.read(data, { type: 'array' });
                    
                    // Find DEL_SCHE sheet
                    const sheetName = workbook.SheetNames.find(name => 
                        name.includes('DEL_SCHE') || name.includes('DEL')
                    );
                    
                    if (!sheetName) {
                        reject(new Error('DEL_SCHE sheet not found'));
                        return;
                    }
                    
                    const worksheet = workbook.Sheets[sheetName];
                    const jsonData = XLSX.utils.sheet_to_json(worksheet, { header: 1 });
                    
                    // Parse schedule data
                    const parsed = this.parseScheduleData(jsonData);
                    resolve(parsed);
                    
                } catch (error) {
                    reject(error);
                }
            };
            
            reader.onerror = reject;
            reader.readAsArrayBuffer(file);
        });
    },
    
    /**
     * Parse schedule data from raw Excel rows
     */
    parseScheduleData(rows) {
        const schedule = {
            date: null,
            deliveries: [],
            moList: [],
            totalQuantity: 0
        };
        
        // Find header row
        const headerRow = rows.find(row => 
            row && row.some(cell => cell && cell.toString().includes('ORDNO'))
        );
        
        if (!headerRow) {
            throw new Error('Header row not found - looking for ORDNO column');
        }
        
        const headerIndex = rows.indexOf(headerRow);
        
        // Map column names to indices
        const colMap = {};
        headerRow.forEach((cell, idx) => {
            if (cell) colMap[cell.toString().trim()] = idx;
        });
        
        // Get column indices
        const moCol = colMap['ORDNO'];
        const dateCol = colMap['DELIVERY_DATE'];
        const timeCol = colMap['DELIVERY_TIME'];
        const qtyCol = colMap['QTREQ'];
        
        if (moCol === undefined) {
            throw new Error('ORDNO column not found in Excel file');
        }
        
        // Parse data rows
        for (let i = headerIndex + 1; i < rows.length; i++) {
            const row = rows[i];
            if (!row || row.length === 0) continue;
            
            const moNumber = row[moCol];
            if (!moNumber || moNumber.toString().includes('Grand Total')) continue;
            
            const moStr = moNumber.toString().trim();
            
            // Set date from first valid row
            if (!schedule.date && dateCol !== undefined && row[dateCol]) {
                schedule.date = row[dateCol];
            }
            
            // Add to deliveries
            schedule.deliveries.push({
                moNumber: moStr,
                date: dateCol !== undefined ? row[dateCol] : '',
                time: timeCol !== undefined ? row[timeCol] : '',
                quantity: qtyCol !== undefined ? (parseInt(row[qtyCol]) || 0) : 0
            });
            
            // Add to unique MO list
            if (!schedule.moList.includes(moStr)) {
                schedule.moList.push(moStr);
            }
            
            if (qtyCol !== undefined) {
                schedule.totalQuantity += parseInt(row[qtyCol]) || 0;
            }
        }
        
        this.scheduledMOs = schedule;
        return schedule;
    },
    
    /**
     * Compare scheduled MOs with WIP system - WITH DATE+TIME COMPARISON
     */
    analyzeSchedule() {
        if (!this.scheduledMOs || this.scheduledMOs.moList.length === 0) {
            return null;
        }
        
        // Get all MOs currently in WIP
        const wipMOs = WIPManager.getAll().flatMap(card => 
            card.moNumbers || [card.moNumber]
        );
        
        const analysis = {
            total: this.scheduledMOs.moList.length,
            inWIP: [],
            missing: [],
            upcoming: [], // MOs needed in the next hour
            urgent: [],   // MOs needed NOW or OVERDUE
            overdue: []   // MOs that are past delivery date
        };
        
        const now = new Date();
        const currentDate = new Date(now.getFullYear(), now.getMonth(), now.getDate());
        const currentHour = now.getHours();
        const currentMinute = now.getMinutes();
        
        console.log(`📅 Current date/time: ${currentDate.toLocaleDateString('vi-VN')} ${currentHour}:${String(currentMinute).padStart(2, '0')}`);
        
        this.scheduledMOs.deliveries.forEach(delivery => {
            const isInWIP = wipMOs.includes(delivery.moNumber);
            
            // Parse delivery date
            let deliveryDate = null;
            if (delivery.date) {
                deliveryDate = this.parseDate(delivery.date);
            }
            
            // Parse delivery time (format: "10:00 - 11:00" or "00:00 - 02:00")
            let deliveryStartHour = null;
            let deliveryEndHour = null;
            
            if (delivery.time) {
                const timeMatch = delivery.time.match(/(\d+):(\d+)\s*-\s*(\d+):(\d+)/);
                if (timeMatch) {
                    deliveryStartHour = parseInt(timeMatch[1]);
                    deliveryEndHour = parseInt(timeMatch[3]);
                }
            }
            
            // Determine urgency with DATE comparison
            let isUpcoming = false;
            let isUrgent = false;
            let isOverdue = false;
            
            if (deliveryDate && deliveryStartHour !== null) {
                // Create full delivery datetime
                const deliveryDateTime = new Date(deliveryDate);
                deliveryDateTime.setHours(deliveryStartHour, 0, 0, 0);
                
                const deliveryDateOnly = new Date(deliveryDate.getFullYear(), deliveryDate.getMonth(), deliveryDate.getDate());
                
                // Compare dates
                const daysDiff = Math.floor((deliveryDateOnly - currentDate) / (1000 * 60 * 60 * 24));
                
                if (daysDiff < 0) {
                    // OVERDUE - Delivery date has passed
                    isOverdue = true;
                    isUrgent = true;
                } else if (daysDiff === 0) {
                    // TODAY - Compare hours
                    const hoursUntilDelivery = deliveryStartHour - currentHour;
                    
                    if (currentHour >= deliveryStartHour && currentHour < deliveryEndHour) {
                        // Delivery happening NOW
                        isUrgent = true;
                    } else if (currentHour > deliveryEndHour) {
                        // Delivery time passed today
                        isOverdue = true;
                        isUrgent = true;
                    } else if (hoursUntilDelivery <= 1) {
                        // Within next hour
                        isUpcoming = true;
                    }
                } else if (daysDiff === 1 && deliveryStartHour <= 2) {
                    // Tomorrow early morning (00:00-02:00) - treat as upcoming
                    isUpcoming = true;
                }
                // Future deliveries (daysDiff > 1) - do nothing
            }
            
            // Categorize delivery
            const deliveryInfo = {
                ...delivery,
                isUpcoming,
                isUrgent,
                isOverdue,
                deliveryDate: deliveryDate ? deliveryDate.toLocaleDateString('vi-VN') : null,
                daysDiff: deliveryDate ? Math.floor((new Date(deliveryDate.getFullYear(), deliveryDate.getMonth(), deliveryDate.getDate()) - currentDate) / (1000 * 60 * 60 * 24)) : null
            };
            
            if (isInWIP) {
                analysis.inWIP.push(deliveryInfo);
            } else {
                analysis.missing.push(deliveryInfo);
                
                // Add to urgent/upcoming/overdue lists if not in WIP
                if (isOverdue) {
                    analysis.overdue.push(deliveryInfo);
                    analysis.urgent.push(deliveryInfo); // Overdue items are also urgent
                } else if (isUrgent) {
                    analysis.urgent.push(deliveryInfo);
                } else if (isUpcoming) {
                    analysis.upcoming.push(deliveryInfo);
                }
            }
        });
        
        // Sort overdue by how late they are (oldest first)
        analysis.overdue.sort((a, b) => {
            if (a.daysDiff !== b.daysDiff) return a.daysDiff - b.daysDiff; // More days overdue first
            return (a.deliveryStartHour || 0) - (b.deliveryStartHour || 0);
        });
        
        console.log('📊 Analysis:', {
            total: analysis.total,
            inWIP: analysis.inWIP.length,
            missing: analysis.missing.length,
            upcoming: analysis.upcoming.length,
            urgent: analysis.urgent.length,
            overdue: analysis.overdue.length
        });
        
        return analysis;
    },

    /**
     * Parse date string to Date object
     */
    parseDate(dateStr) {
        if (!dateStr) return null;
        
        const str = dateStr.toString().trim();
        
        // Try direct parse
        let date = new Date(str);
        if (!isNaN(date.getTime())) return date;
        
        // Try M/D/YYYY or MM/DD/YYYY format (common in Excel)
        const parts = str.split('/');
        if (parts.length === 3) {
            const month = parseInt(parts[0]) - 1; // Month is 0-indexed
            const day = parseInt(parts[1]);
            const year = parseInt(parts[2]);
            
            // Handle 2-digit years (26 → 2026)
            const fullYear = year < 100 ? 2000 + year : year;
            
            date = new Date(fullYear, month, day);
            if (!isNaN(date.getTime())) return date;
        }
        
        return null;
    }
};
