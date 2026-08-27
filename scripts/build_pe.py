import struct, subprocess, os

def build_pe(elf_path, out_exe, imports_def, entry_func_name="entry_point", is_gui=True, extra_blobs=None):
    if extra_blobs is None:
        extra_blobs = {}

    with open(elf_path, "rb") as f:
        elf = f.read()

    # ELF64 header
    e_shoff = struct.unpack_from("<Q", elf, 40)[0]
    e_shentsize = struct.unpack_from("<H", elf, 58)[0]
    e_shnum = struct.unpack_from("<H", elf, 60)[0]
    e_shstrndx = struct.unpack_from("<H", elf, 62)[0]

    shdrs = []
    for i in range(e_shnum):
        off = e_shoff + i * e_shentsize
        sh_name, sh_type, sh_flags, sh_addr, sh_offset, sh_size, sh_link, sh_info, sh_addralign, sh_entsize = struct.unpack_from("<IIQQQQIIQQ", elf, off)
        shdrs.append({
            "idx": i, "name_off": sh_name, "type": sh_type, "flags": sh_flags,
            "addr": sh_addr, "offset": sh_offset, "size": sh_size,
            "link": sh_link, "info": sh_info, "addralign": sh_addralign, "entsize": sh_entsize
        })

    shstr_hdr = shdrs[e_shstrndx]
    shstr = elf[shstr_hdr["offset"] : shstr_hdr["offset"] + shstr_hdr["size"]]
    for s in shdrs:
        name_end = shstr.find(b"\0", s["name_off"])
        s["name"] = shstr[s["name_off"]:name_end].decode()

    # Find sections
    text_hdr = next((s for s in shdrs if s["name"] == ".text"), None)
    symtab_hdr = next((s for s in shdrs if s["name"] == ".symtab"), None)
    strtab_hdr = next((s for s in shdrs if s["name"] == ".strtab"), None)
    rela_text_hdr = next((s for s in shdrs if s["name"] == ".rela.text"), None)

    text_data = bytearray(elf[text_hdr["offset"]:text_hdr["offset"]+text_hdr["size"]]) if text_hdr else bytearray()

    # Read strtab
    strtab = elf[strtab_hdr["offset"]:strtab_hdr["offset"]+strtab_hdr["size"]] if strtab_hdr else b""

    # Read symtab
    syms = []
    if symtab_hdr:
        sym_entsize = symtab_hdr["entsize"] or 24
        sym_count = symtab_hdr["size"] // sym_entsize
        for i in range(sym_count):
            off = symtab_hdr["offset"] + i * sym_entsize
            st_name, st_info, st_other, st_shndx, st_value, st_size = struct.unpack_from("<IBBHQQ", elf, off)
            name_end = strtab.find(b"\0", st_name)
            name = strtab[st_name:name_end].decode()
            syms.append({"idx": i, "name": name, "shndx": st_shndx, "value": st_value, "size": st_size})

    # Read rela.text
    relas = []
    if rela_text_hdr:
        r_entsize = rela_text_hdr["entsize"] or 24
        r_count = rela_text_hdr["size"] // r_entsize
        for i in range(r_count):
            off = rela_text_hdr["offset"] + i * r_entsize
            r_offset, r_info, r_addend = struct.unpack_from("<QQq", elf, off)
            r_sym = r_info >> 32
            r_type = r_info & 0xFFFFFFFF
            relas.append({"offset": r_offset, "sym_idx": r_sym, "type": r_type, "addend": r_addend})

    IMAGE_BASE = 0x0000000140000000
    SECTION_ALIGN = 0x1000
    FILE_ALIGN = 0x200

    TEXT_RVA = 0x1000
    text_raw_size = ((len(text_data) + FILE_ALIGN - 1) // FILE_ALIGN) * FILE_ALIGN
    text_virt_size = ((len(text_data) + SECTION_ALIGN - 1) // SECTION_ALIGN) * SECTION_ALIGN

    RDATA_RVA = TEXT_RVA + max(text_virt_size, SECTION_ALIGN)

    # In .rdata:
    # 1. IAT
    # 2. Import Directory Table
    # 3. INT
    # 4. Hint/Names
    # 5. DLL names
    # 6. Mapped data/rodata sections from ELF
    # 7. Extra blobs (embedded files)

    total_funcs = sum(len(funcs) for _, funcs in imports_def)
    iat_size = (total_funcs + len(imports_def)) * 8
    
    rdata = bytearray()
    iat_offsets = {}
    current_iat_offset = 0
    
    for dll_name, funcs in imports_def:
        for f in funcs:
            iat_offsets[f] = current_iat_offset
            current_iat_offset += 8
        current_iat_offset += 8
    
    rdata.extend(b"\x00" * current_iat_offset)

    import_dir_offset = len(rdata)
    desc_table_size = (len(imports_def) + 1) * 20
    rdata.extend(b"\x00" * desc_table_size)

    int_offsets = {}
    for dll_name, funcs in imports_def:
        int_offsets[dll_name] = len(rdata)
        rdata.extend(b"\x00" * ((len(funcs) + 1) * 8))

    hint_name_offsets = {}
    for dll_name, funcs in imports_def:
        for f in funcs:
            hint_name_offsets[f] = len(rdata)
            entry = struct.pack("<H", 0) + f.encode("ascii") + b"\x00"
            if len(entry) % 2 != 0:
                entry += b"\x00"
            rdata.extend(entry)

    dll_name_offsets = {}
    for dll_name, funcs in imports_def:
        dll_name_offsets[dll_name] = len(rdata)
        rdata.extend(dll_name.encode("ascii") + b"\x00")

    for i, (dll_name, funcs) in enumerate(imports_def):
        desc_pos = import_dir_offset + i * 20
        orig_first_thunk_rva = RDATA_RVA + int_offsets[dll_name]
        dll_name_rva = RDATA_RVA + dll_name_offsets[dll_name]
        first_thunk_rva = RDATA_RVA + iat_offsets[funcs[0]]

        struct.pack_into("<IIIII", rdata, desc_pos,
                         orig_first_thunk_rva, 0, 0, dll_name_rva, first_thunk_rva)

        int_pos = int_offsets[dll_name]
        for j, f in enumerate(funcs):
            hn_rva = RDATA_RVA + hint_name_offsets[f]
            struct.pack_into("<Q", rdata, int_pos + j * 8, hn_rva)
            iat_pos = iat_offsets[f]
            struct.pack_into("<Q", rdata, iat_pos, hn_rva)

    # Append all data and rodata sections from ELF
    sec_offset_in_rdata = {}
    for s in shdrs:
        if s["name"].startswith(".rodata") or s["name"].startswith(".data"):
            pad = (16 - (len(rdata) % 16)) % 16
            rdata.extend(b"\x00" * pad)
            sec_offset_in_rdata[s["idx"]] = len(rdata)
            s_data = elf[s["offset"]:s["offset"]+s["size"]]
            rdata.extend(s_data)

    # Append extra blobs
    blob_offsets = {}
    for bname, bdata in extra_blobs.items():
        pad = (16 - (len(rdata) % 16)) % 16
        rdata.extend(b"\x00" * pad)
        blob_offsets[bname] = len(rdata)
        rdata.extend(bdata)

    rdata_raw_size = ((len(rdata) + FILE_ALIGN - 1) // FILE_ALIGN) * FILE_ALIGN
    rdata_virt_size = ((len(rdata) + SECTION_ALIGN - 1) // SECTION_ALIGN) * SECTION_ALIGN

    # 3. Resolve Relocations in text_data
    text_idx = text_hdr["idx"] if text_hdr else -1
    for r in relas:
        sym = syms[r["sym_idx"]]
        sym_name = sym["name"]
        patch_pos = r["offset"]

        target_rva = 0
        if sym_name == "apis":
            target_rva = RDATA_RVA
        elif sym_name in blob_offsets:
            target_rva = RDATA_RVA + blob_offsets[sym_name]
        elif sym_name + "_size" in blob_offsets:
            target_rva = RDATA_RVA + blob_offsets[sym_name + "_size"]
        elif sym["shndx"] in sec_offset_in_rdata:
            target_rva = RDATA_RVA + sec_offset_in_rdata[sym["shndx"]] + sym["value"]
        elif sym["shndx"] == text_idx:
            target_rva = TEXT_RVA + sym["value"]
        else:
            print(f"Warning: unresolved relocation symbol '{sym_name}'")
            continue

        instr_pc_rva = TEXT_RVA + patch_pos
        disp = (target_rva + r["addend"]) - (instr_pc_rva + 4)
        struct.pack_into("<i", text_data, patch_pos, disp)

    # Find entry point
    entry_sym = next((s for s in syms if s["name"] == entry_func_name), None)
    entry_point_rva = TEXT_RVA + (entry_sym["value"] if entry_sym else 0)

    # 4. Build PE File Headers
    HEADER_SIZE = 0x400
    TEXT_RAW_PTR = HEADER_SIZE
    RDATA_RAW_PTR = TEXT_RAW_PTR + text_raw_size
    SIZE_OF_IMAGE = RDATA_RVA + rdata_virt_size

    pe_file = bytearray(HEADER_SIZE)

    # DOS Header
    pe_file[0:2] = b"MZ"
    struct.pack_into("<H", pe_file, 0x3C, 0x80)

    stub = b"\x0e\x1f\xba\x0e\x00\xb4\x09\xcd\x21\xb8\x01\x4c\xcd\x21This program cannot be run in DOS mode.\r\r\n$\x00\x00\x00"
    pe_file[0x40:0x40+len(stub)] = stub

    # PE Signature
    pe_file[0x80:0x84] = b"PE\x00\x00"

    # COFF File Header
    struct.pack_into("<HHIIIHH", pe_file, 0x84,
                     0x8664, 2, 0, 0, 0, 240, 0x0022)

    # Optional Header (PE32+)
    opt_off = 0x98
    subsystem = 2 if is_gui else 3
    struct.pack_into('<H', pe_file, opt_off + 0, 0x020B)
    struct.pack_into('<B', pe_file, opt_off + 2, 14)
    struct.pack_into('<B', pe_file, opt_off + 3, 0)
    struct.pack_into('<I', pe_file, opt_off + 4, text_raw_size)
    struct.pack_into('<I', pe_file, opt_off + 8, rdata_raw_size)
    struct.pack_into('<I', pe_file, opt_off + 12, 0)
    struct.pack_into('<I', pe_file, opt_off + 16, entry_point_rva)
    struct.pack_into('<I', pe_file, opt_off + 20, TEXT_RVA)
    struct.pack_into('<Q', pe_file, opt_off + 24, IMAGE_BASE)
    struct.pack_into('<I', pe_file, opt_off + 32, SECTION_ALIGN)
    struct.pack_into('<I', pe_file, opt_off + 36, FILE_ALIGN)
    struct.pack_into('<H', pe_file, opt_off + 40, 6)
    struct.pack_into('<H', pe_file, opt_off + 42, 0)
    struct.pack_into('<H', pe_file, opt_off + 44, 1)
    struct.pack_into('<H', pe_file, opt_off + 46, 0)
    struct.pack_into('<H', pe_file, opt_off + 48, 6)
    struct.pack_into('<H', pe_file, opt_off + 50, 0)
    struct.pack_into('<I', pe_file, opt_off + 52, 0)
    struct.pack_into('<I', pe_file, opt_off + 56, SIZE_OF_IMAGE)
    struct.pack_into('<I', pe_file, opt_off + 60, HEADER_SIZE)
    struct.pack_into('<I', pe_file, opt_off + 64, 0)
    struct.pack_into('<H', pe_file, opt_off + 68, subsystem)
    struct.pack_into('<H', pe_file, opt_off + 70, 0x8160)
    struct.pack_into('<Q', pe_file, opt_off + 72, 0x100000)
    struct.pack_into('<Q', pe_file, opt_off + 80, 0x1000)
    struct.pack_into('<Q', pe_file, opt_off + 88, 0x100000)
    struct.pack_into('<Q', pe_file, opt_off + 96, 0x1000)
    struct.pack_into('<I', pe_file, opt_off + 104, 0)
    struct.pack_into('<I', pe_file, opt_off + 108, 16)

    # Data Directories
    dd_off = opt_off + 112
    # Import Table
    struct.pack_into("<II", pe_file, dd_off + 1 * 8, RDATA_RVA + import_dir_offset, desc_table_size)
    # IAT
    struct.pack_into("<II", pe_file, dd_off + 12 * 8, RDATA_RVA, iat_size)

    # Section Headers
    sec_off = opt_off + 240
    struct.pack_into("<8sIIIIIIHHI", pe_file, sec_off,
                     b".text\x00\x00\x00",
                     len(text_data), TEXT_RVA, text_raw_size, TEXT_RAW_PTR,
                     0, 0, 0, 0, 0x60000020)

    struct.pack_into("<8sIIIIIIHHI", pe_file, sec_off + 40,
                     b".rdata\x00\x00\x00",
                     len(rdata), RDATA_RVA, rdata_raw_size, RDATA_RAW_PTR,
                     0, 0, 0, 0, 0x40000040)

    pe_final = bytearray(pe_file)
    pe_final.extend(text_data)
    pe_final.extend(b"\x00" * (text_raw_size - len(text_data)))
    pe_final.extend(rdata)
    pe_final.extend(b"\x00" * (rdata_raw_size - len(rdata)))

    os.makedirs(os.path.dirname(os.path.abspath(out_exe)), exist_ok=True)
    with open(out_exe, "wb") as f:
        f.write(pe_final)
    print(f"Successfully generated: {out_exe} ({len(pe_final)} bytes, subsystem={'GUI' if is_gui else 'Console'})")
    return len(pe_final)

