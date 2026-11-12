(module
  (import "wasi_snapshot_preview1" "fd_write" (func $fd_write (param i32 i32 i32 i32) (result i32)))
  (memory 1)
  (export "memory" (memory 0))
  (data (i32.const 128) "dbg: ")
  (data (i32.const 224) "\0a")
  (func $dbg_str (param $ptr i32)
    (local $len i32)
    local.get $ptr
    i32.const 4
    i32.sub
    i32.load
    local.set $len
    i32.const 0
    i32.const 128
    i32.store
    i32.const 4
    i32.const 5
    i32.store
    i32.const 1
    i32.const 0
    i32.const 1
    i32.const 8
    call $fd_write
    drop
    i32.const 0
    local.get $ptr
    i32.store
    i32.const 4
    local.get $len
    i32.store
    i32.const 1
    i32.const 0
    i32.const 1
    i32.const 8
    call $fd_write
    drop
    i32.const 0
    i32.const 224
    i32.store
    i32.const 4
    i32.const 1
    i32.store
    i32.const 1
    i32.const 0
    i32.const 1
    i32.const 8
    call $fd_write
    drop
  )

  (global $heap_ptr (mut i32) (i32.const 4132))

  (func $__nanyx_alloc (param $size i32) (result i32)
    (local $link i32)
    (local $ptr i32)
    local.get $size
    i32.const 3
    i32.add
    i32.const -4
    i32.and
    local.tee $size
    i32.eqz
    if
      i32.const 4
      local.set $size
    end
    local.get $size
    i32.const 32
    i32.le_u
    if (result i32)
      i32.const 4092
      local.get $size
      i32.add
    else
      i32.const 4128
    end
    local.set $link
    block $empty
      loop $search
        local.get $link
        i32.load
        local.tee $ptr
        i32.eqz
        br_if $empty
        local.get $ptr
        i32.const 4
        i32.sub
        i32.load
        local.get $size
        i32.ge_u
        if
          local.get $link
          local.get $ptr
          i32.load
          i32.store
          local.get $ptr
          return
        end
        local.get $ptr
        local.set $link
        br $search
      end
    end
    global.get $heap_ptr
    i32.const 4
    i32.add
    local.tee $ptr
    local.get $size
    i32.add
    global.set $heap_ptr
    block $fits
      loop $grow
        global.get $heap_ptr
        memory.size
        i32.const 65536
        i32.mul
        i32.le_u
        br_if $fits
        i32.const 1
        memory.grow
        i32.const -1
        i32.eq
        if
          unreachable
        end
        br $grow
      end
    end
    local.get $ptr
    i32.const 4
    i32.sub
    local.get $size
    i32.store
    local.get $ptr
  )
  (func $__nanyx_free (param $ptr i32)
    (local $size i32)
    (local $head i32)
    local.get $ptr
    i32.const 4
    i32.sub
    i32.load
    local.set $size
    local.get $size
    i32.const 32
    i32.le_u
    if (result i32)
      i32.const 4092
      local.get $size
      i32.add
    else
      i32.const 4128
    end
    local.set $head
    local.get $ptr
    local.get $head
    i32.load
    i32.store
    local.get $head
    local.get $ptr
    i32.store
  )
  (func $__nanyx_new_object (param $size i32) (param $info i32) (result i32)
    (local $block i32)
    local.get $size
    i32.const 8
    i32.add
    call $__nanyx_alloc
    local.tee $block
    i32.const 1
    i32.store
    local.get $block
    local.get $info
    i32.store offset=4
    local.get $block
    i32.const 8
    i32.add
  )
  (func $__nanyx_release (param $count i32) (result i32)
    (local $n i32)
    local.get $count
    i32.load
    local.tee $n
    i32.eqz
    if
      i32.const 0
      return
    end
    local.get $n
    i32.const 1
    i32.eq
    if
      i32.const 1
      return
    end
    local.get $count
    local.get $n
    i32.const 1
    i32.sub
    i32.store
    i32.const 0
  )
  (func $__nanyx_retain (param $count i32)
    (local $n i32)
    local.get $count
    i32.load
    local.tee $n
    if
      local.get $count
      local.get $n
      i32.const 1
      i32.add
      i32.store
    end
  )
  (func $__nanyx_string_length (param $s i32) (result i32)
    local.get $s
    i32.const 4
    i32.sub
    i32.load
  )
  (func $__nanyx_string_alloc (param $len i32) (result i32)
    (local $s i32)
    local.get $len
    i32.const 1
    i32.add
    local.get $len
    call $__nanyx_new_object
    local.tee $s
    local.get $len
    i32.add
    i32.const 0
    i32.store8
    local.get $s
  )
  (func $__nanyx_record_set (param $base i32) (param $value i32) (param $offset i32) (result i32)
    local.get $base
    local.get $offset
    i32.add
    local.get $value
    i32.store
    local.get $base
  )
  (func $__nanyx_record_set_i64 (param $base i32) (param $value i64) (param $offset i32) (result i32)
    local.get $base
    local.get $offset
    i32.add
    local.get $value
    i64.store
    local.get $base
  )
  (func $__nanyx_escape_byte (param $c i32) (result i32)
    local.get $c
    i32.const 34
    i32.eq
    if
      i32.const 34
      return
    end
    local.get $c
    i32.const 92
    i32.eq
    if
      i32.const 92
      return
    end
    local.get $c
    i32.const 10
    i32.eq
    if
      i32.const 110
      return
    end
    local.get $c
    i32.const 13
    i32.eq
    if
      i32.const 114
      return
    end
    local.get $c
    i32.const 9
    i32.eq
    if
      i32.const 116
      return
    end
    i32.const 0
  )
  (func $__nanyx_string_quote (param $s i32) (result i32)
    (local $len i32)
    (local $i i32)
    (local $n i32)
    (local $out i32)
    (local $at i32)
    (local $c i32)
    (local $e i32)
    local.get $s
    call $__nanyx_string_length
    local.set $len
    i32.const 2
    local.set $n
    block $measured
      loop $measure
        local.get $i
        local.get $len
        i32.ge_s
        br_if $measured
        local.get $n
        i32.const 1
        i32.add
        local.get $s
        local.get $i
        i32.add
        i32.load8_u
        call $__nanyx_escape_byte
        i32.const 0
        i32.ne
        i32.add
        local.set $n
        local.get $i
        i32.const 1
        i32.add
        local.set $i
        br $measure
      end
    end
    local.get $n
    call $__nanyx_string_alloc
    local.tee $out
    i32.const 34
    i32.store8
    local.get $out
    i32.const 1
    i32.add
    local.set $at
    i32.const 0
    local.set $i
    block $written
      loop $write
        local.get $i
        local.get $len
        i32.ge_s
        br_if $written
        local.get $s
        local.get $i
        i32.add
        i32.load8_u
        local.tee $c
        call $__nanyx_escape_byte
        local.tee $e
        if
          local.get $at
          i32.const 92
          i32.store8
          local.get $at
          i32.const 1
          i32.add
          local.tee $at
          local.get $e
          i32.store8
        else
          local.get $at
          local.get $c
          i32.store8
        end
        local.get $at
        i32.const 1
        i32.add
        local.set $at
        local.get $i
        i32.const 1
        i32.add
        local.set $i
        br $write
      end
    end
    local.get $at
    i32.const 34
    i32.store8
    local.get $out
  )

  (data (i32.const 256) "\00\00\00\00\05\00\00\00Alice\00\00\00")
  (data (i32.const 272) "\00\00\00\00\03\00\00\00Bob\00")

  (func $printPerson (param $p i32) (result i32) (local $__dbg_value i32) (local $__dbg_value_1_i64 i64) (local $__dbg_value_2_i64 i64) (local $__owned_0 i32) (local $__owned_1 i32) (local $__owned_2 i32)
    local.get $p
    i32.load offset=8
    call $__nanyx_dup_string
    local.set $__dbg_value
    local.get $__dbg_value
    call $__dbg_show_string
    local.tee $__owned_0
    call $dbg_str
    i32.const 0
    local.get $__owned_0
    call $__nanyx_drop_string
    drop
    local.get $__dbg_value
    call $__nanyx_drop_string
    local.get $p
    i64.load
    local.set $__dbg_value_1_i64
    local.get $__dbg_value_1_i64
    call $__dbg_show_int
    local.tee $__owned_1
    call $dbg_str
    i32.const 0
    local.get $__owned_1
    call $__nanyx_drop_string
    drop
    local.get $__dbg_value_1_i64
    drop
    local.get $p
    i64.load offset=12
    local.get $p
    call $__nanyx_drop_rec_age_int_name_string_score_int
    local.set $__dbg_value_2_i64
    local.get $__dbg_value_2_i64
    call $__dbg_show_int
    local.tee $__owned_2
    call $dbg_str
    i32.const 0
    local.get $__owned_2
    call $__nanyx_drop_string
    drop
    local.get $__dbg_value_2_i64
    drop
    i32.const 0
  )
  (export "printPerson" (func $printPerson))

  (func $main (result i32) (local $p i32) (local $p2 i32)
    i32.const 20
    i32.const 1
    call $__nanyx_new_object
    i64.const 30
    i32.const 0
    call $__nanyx_record_set_i64
    i32.const 264
    i32.const 8
    call $__nanyx_record_set
    i64.const 100
    i32.const 12
    call $__nanyx_record_set_i64
    local.set $p
    local.get $p
    call $printPerson
    drop
    i32.const 20
    i32.const 1
    call $__nanyx_new_object
    i64.const 25
    i32.const 0
    call $__nanyx_record_set_i64
    i32.const 280
    i32.const 8
    call $__nanyx_record_set
    i64.const 80
    i32.const 12
    call $__nanyx_record_set_i64
    local.set $p2
    local.get $p2
    return_call $printPerson
  )
  (export "main" (func $main))

  (func $__dbg_show_int (param $__dbg_value_i64 i64) (result i32) (local $__interp_len i32) (local $__interp_base i32) (local $__interp_cursor i32) (local $__interp_ptr i32) (local $__interp_copy_len i32) (local $__interp_src i32) (local $__interp_start i32) (local $__interp_lo i32) (local $__interp_hi i32) (local $__interp_swap i32) (local $__interp_int i64)
    i32.const 0
    local.set $__interp_len
    local.get $__dbg_value_i64
    local.set $__interp_int
    local.get $__interp_int
    i64.const 0
    i64.lt_s
    if
      local.get $__interp_len
      i32.const 1
      i32.add
      local.set $__interp_len
      i64.const 0
      local.get $__interp_int
      i64.sub
      local.set $__interp_int
    end
    local.get $__interp_int
    i64.eqz
    if
      local.get $__interp_len
      i32.const 1
      i32.add
      local.set $__interp_len
    else
      loop $interp_int_len
        local.get $__interp_len
        i32.const 1
        i32.add
        local.set $__interp_len
        local.get $__interp_int
        i64.const 10
        i64.div_u
        local.set $__interp_int
        local.get $__interp_int
        i64.const 0
        i64.ne
        br_if $interp_int_len
      end
    end
    local.get $__interp_len
    call $__nanyx_string_alloc
    local.tee $__interp_base
    local.set $__interp_cursor
    local.get $__dbg_value_i64
    local.set $__interp_int
    local.get $__interp_int
    i64.const 0
    i64.lt_s
    if
      local.get $__interp_cursor
      i32.const 45
      i32.store8
      local.get $__interp_cursor
      i32.const 1
      i32.add
      local.set $__interp_cursor
      i64.const 0
      local.get $__interp_int
      i64.sub
      local.set $__interp_int
    end
    local.get $__interp_int
    i64.eqz
    if
      local.get $__interp_cursor
      i32.const 48
      i32.store8
      local.get $__interp_cursor
      i32.const 1
      i32.add
      local.set $__interp_cursor
    else
      local.get $__interp_cursor
      local.set $__interp_start
      loop $interp_digits
        local.get $__interp_cursor
        local.get $__interp_int
        i64.const 10
        i64.rem_u
        i32.wrap_i64
        i32.const 48
        i32.add
        i32.store8
        local.get $__interp_cursor
        i32.const 1
        i32.add
        local.set $__interp_cursor
        local.get $__interp_int
        i64.const 10
        i64.div_u
        local.set $__interp_int
        local.get $__interp_int
        i64.const 0
        i64.ne
        br_if $interp_digits
      end
      local.get $__interp_start
      local.set $__interp_lo
      local.get $__interp_cursor
      i32.const 1
      i32.sub
      local.set $__interp_hi
      block $interp_rev_done
        loop $interp_rev
          local.get $__interp_lo
          local.get $__interp_hi
          i32.ge_u
          br_if $interp_rev_done
          local.get $__interp_lo
          i32.load8_u
          local.set $__interp_swap
          local.get $__interp_lo
          local.get $__interp_hi
          i32.load8_u
          i32.store8
          local.get $__interp_hi
          local.get $__interp_swap
          i32.store8
          local.get $__interp_lo
          i32.const 1
          i32.add
          local.set $__interp_lo
          local.get $__interp_hi
          i32.const 1
          i32.sub
          local.set $__interp_hi
          br $interp_rev
        end
      end
    end
    local.get $__interp_base
  )

  (func $__dbg_show_string (param $__dbg_value i32) (result i32)
    local.get $__dbg_value
    call $__nanyx_string_quote
  )

  (func $__nanyx_drop_rec_age_int_name_string_score_int (param $p i32)
    ;; (age: int, name: string, score: int)
    local.get $p
    i32.const 8
    i32.sub
    call $__nanyx_release
    if
      local.get $p
      i32.load offset=8
      call $__nanyx_drop_string
      local.get $p
      i32.const 8
      i32.sub
      call $__nanyx_free
    end
  )

  (func $__nanyx_dup_rec_age_int_name_string_score_int (param $p i32) (result i32)
    block $shared
      local.get $p
      i32.const 8
      i32.sub
      call $__nanyx_retain
    end
    local.get $p
  )

  (func $__nanyx_drop_string (param $p i32)
    ;; string
    local.get $p
    i32.const 8
    i32.sub
    call $__nanyx_release
    if
      local.get $p
      i32.const 8
      i32.sub
      call $__nanyx_free
    end
  )

  (func $__nanyx_dup_string (param $p i32) (result i32)
    block $shared
      local.get $p
      i32.const 8
      i32.sub
      call $__nanyx_retain
    end
    local.get $p
  )
)