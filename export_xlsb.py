import sys
import json
import os
import traceback

def create_xlsb(json_data_path, template_path, output_path):
    excel = None
    wb = None
    try:
        import win32com.client as win32

        with open(json_data_path, 'r', encoding='utf-8') as f:
            data = json.load(f)
            
        received_mx = set(data.get('received_mx', []))

        excel = win32.DispatchEx('Excel.Application')
        excel.Visible = False
        excel.DisplayAlerts = False
        excel.AskToUpdateLinks = False

        wb = excel.Workbooks.Open(os.path.abspath(template_path), UpdateLinks=0, ReadOnly=True)
        
        try:
            ws = wb.Sheets('Print')
        except:
            ws = wb.Sheets(1) 
        last_row = ws.Cells(ws.Rows.Count, "D").End(-4162).Row 
        
        for r in range(5, last_row + 1):
            mx_val = ws.Cells(r, 4).Value 
            if mx_val:
                mx_code = str(mx_val).strip().upper()
                if mx_code in received_mx:
                    # Điền chữ OK vào cột K (Cột 11)
                    cell_k = ws.Cells(r, 11)
                    cell_k.Value = "OK"
                    cell_k.Font.Bold = True
                    cell_k.Font.Color = 0x008000 # Màu Xanh lá
        wb.SaveAs(os.path.abspath(output_path), FileFormat=50, ConflictResolution=2)
        print(f"SUCCESS|{output_path}")

    except ImportError:
        print("ERROR|Thieu thu vien pywin32. Vui long chay lenh: pip install pywin32")
    except Exception as e:
        error_details = traceback.format_exc()
        print(f"ERROR|{str(e)} | Chi tiet: {error_details}")
    finally:
        if wb:
            try: wb.Close(False)
            except: pass
        if excel:
            try: excel.Quit()
            except: pass

if __name__ == "__main__":
    if len(sys.argv) != 4:
        print("ERROR|Missing arguments")
        sys.exit(1)
        
    sys.stdout.reconfigure(encoding='utf-8')
    
    create_xlsb(sys.argv[1], sys.argv[2], sys.argv[3])
