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
  (func $__nanyx_copy_bytes (param $dst i32) (param $src i32) (param $len i32)
    (local $i i32)
    block $done
      loop $copy
        local.get $i
        local.get $len
        i32.ge_s
        br_if $done
        local.get $dst
        local.get $i
        i32.add
        local.get $src
        local.get $i
        i32.add
        i32.load8_u
        i32.store8
        local.get $i
        i32.const 1
        i32.add
        local.set $i
        br $copy
      end
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
  (func $__nanyx_string_concat (param $a i32) (param $b i32) (result i32)
    (local $lenA i32)
    (local $lenB i32)
    (local $out i32)
    local.get $a
    call $__nanyx_string_length
    local.set $lenA
    local.get $b
    call $__nanyx_string_length
    local.set $lenB
    local.get $lenA
    local.get $lenB
    i32.add
    call $__nanyx_string_alloc
    local.set $out
    local.get $out
    local.get $a
    local.get $lenA
    call $__nanyx_copy_bytes
    local.get $out
    local.get $lenA
    i32.add
    local.get $b
    local.get $lenB
    call $__nanyx_copy_bytes
    local.get $out
  )
  (func $__nanyx_string_equals (param $a i32) (param $b i32) (result i32)
    (local $len i32)
    (local $i i32)
    local.get $a
    call $__nanyx_string_length
    local.set $len
    local.get $len
    local.get $b
    call $__nanyx_string_length
    i32.ne
    if
      i32.const 0
      return
    end
    block $done
      loop $compare
        local.get $i
        local.get $len
        i32.ge_s
        br_if $done
        local.get $a
        local.get $i
        i32.add
        i32.load8_u
        local.get $b
        local.get $i
        i32.add
        i32.load8_u
        i32.ne
        if
          i32.const 0
          return
        end
        local.get $i
        i32.const 1
        i32.add
        local.set $i
        br $compare
      end
    end
    i32.const 1
  )
  (func $__nanyx_record_set (param $base i32) (param $value i32) (param $offset i32) (result i32)
    local.get $base
    local.get $offset
    i32.add
    local.get $value
    i32.store
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

  (type $ctx_fn_1 (func (param i32) (result i32)))
  (type $ctx_fn_2 (func (param i32 i32) (result i32)))
  (type $ctx_fn_2_i32_i64_to_i32 (func (param i32 i64) (result i32)))
  (table $ctx_table 2 funcref)
  (elem (i32.const 0) $__lambda_0 $__lambda_2)

  (data (i32.const 256) "\00\00\00\00\09\00\00\00xString: \00\00\00")
  (data (i32.const 276) "\00\00\00\00\0b\00\00\00, yString: \00")
  (data (i32.const 296) "\00\00\00\00\04\00\00\00fake\00\00\00\00")
  (data (i32.const 312) "\00\00\00\00\00\00\00\00\00\00\00\00")
  (data (i32.const 324) "\00\00\00\00\00\00\00\00\01\00\00\00")

  (func $compareStrings__int__string (param $x_i64 i64) (param $y i32) (param $__ctx_ToString_a_toString i32) (param $__ctx_ToString_b_toString i32) (result i32) (local $xString i32) (local $yString i32) (local $__dbg_value i32) (local $__owned_0 i32) (local $__owned_1 i32) (local $__owned_2 i32)
    local.get $__ctx_ToString_a_toString
    local.get $x_i64
    local.get $__ctx_ToString_a_toString
    i32.load
    call_indirect (type $ctx_fn_2_i32_i64_to_i32)
    local.set $xString
    local.get $__ctx_ToString_b_toString
    local.get $y
    local.get $__ctx_ToString_b_toString
    i32.load
    call_indirect (type $ctx_fn_2)
    local.set $yString
    i32.const 264
    local.get $xString
    i32.const 284
    local.get $yString
    call $__nanyx_string_concat
    local.tee $__owned_1
    call $__nanyx_string_concat
    local.get $__owned_1
    call $__nanyx_drop_string
    local.tee $__owned_0
    call $__nanyx_string_concat
    local.get $__owned_0
    call $__nanyx_drop_string
    local.set $__dbg_value
    local.get $__dbg_value
    call $__dbg_show_string
    local.tee $__owned_2
    call $dbg_str
    i32.const 0
    local.get $__owned_2
    call $__nanyx_drop_string
    drop
    local.get $__dbg_value
    call $__nanyx_drop_string
    local.get $xString
    local.get $yString
    call $__nanyx_string_equals
    local.get $xString
    call $__nanyx_drop_string
    local.get $yString
    call $__nanyx_drop_string
  )

  (func $main (result i32) (local $fakeInt i32) (local $__use_ToString_int_toString_1 i32) (local $plainString i32) (local $__use_ToString_string_toString_3 i32)
    i32.const 4
    i32.const 1
    call $__nanyx_new_object
    i32.const 320
    i32.const 0
    call $__nanyx_record_set
    local.set $fakeInt
    local.get $fakeInt
    i32.load
    call $__nanyx_dup_fn_int_to_string
    local.get $fakeInt
    call $__nanyx_drop_rec_toString_fn_int_to_string
    local.set $__use_ToString_int_toString_1
    i32.const 4
    i32.const 1
    call $__nanyx_new_object
    i32.const 332
    i32.const 0
    call $__nanyx_record_set
    local.set $plainString
    local.get $plainString
    i32.load
    call $__nanyx_dup_fn_string_to_string
    local.get $plainString
    call $__nanyx_drop_rec_toString_fn_string_to_string
    local.set $__use_ToString_string_toString_3
    i64.const 42
    i32.const 304
    local.get $__use_ToString_int_toString_1
    local.get $__use_ToString_string_toString_3
    return_call $compareStrings__int__string
  )
  (export "main" (func $main))

  (func $__lambda_0 (param $__env i32) (param $i_i64 i64) (result i32)
    local.get $__env
    call $__nanyx_drop_rec_0_word
    i32.const 304
  )
  (export "__lambda_0" (func $__lambda_0))

  (func $__lambda_2 (param $__env i32) (param $s i32) (result i32)
    local.get $__env
    call $__nanyx_drop_rec_0_word
    local.get $s
  )
  (export "__lambda_2" (func $__lambda_2))

  (func $__dbg_show_string (param $__dbg_value i32) (result i32)
    local.get $__dbg_value
    call $__nanyx_string_quote
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

  (func $__nanyx_drop_fn_int_to_string (param $p i32)
    ;; (int -> string)
    local.get $p
    i32.const 8
    i32.sub
    call $__nanyx_release
    if
      local.get $p
      local.get $p
      i32.const 4
      i32.sub
      i32.load
      call_indirect (type $ctx_fn_1)
      drop
      local.get $p
      i32.const 8
      i32.sub
      call $__nanyx_free
    end
  )

  (func $__nanyx_dup_fn_int_to_string (param $p i32) (result i32)
    block $shared
      local.get $p
      i32.const 8
      i32.sub
      call $__nanyx_retain
    end
    local.get $p
  )

  (func $__nanyx_drop_fn_string_to_string (param $p i32)
    ;; (string -> string)
    local.get $p
    i32.const 8
    i32.sub
    call $__nanyx_release
    if
      local.get $p
      local.get $p
      i32.const 4
      i32.sub
      i32.load
      call_indirect (type $ctx_fn_1)
      drop
      local.get $p
      i32.const 8
      i32.sub
      call $__nanyx_free
    end
  )

  (func $__nanyx_dup_fn_string_to_string (param $p i32) (result i32)
    block $shared
      local.get $p
      i32.const 8
      i32.sub
      call $__nanyx_retain
    end
    local.get $p
  )

  (func $__nanyx_drop_rec_toString_fn_int_to_string (param $p i32)
    ;; (toString: (int -> string))
    local.get $p
    i32.const 8
    i32.sub
    call $__nanyx_release
    if
      local.get $p
      i32.load
      call $__nanyx_drop_fn_int_to_string
      local.get $p
      i32.const 8
      i32.sub
      call $__nanyx_free
    end
  )

  (func $__nanyx_dup_rec_toString_fn_int_to_string (param $p i32) (result i32)
    block $shared
      local.get $p
      i32.const 8
      i32.sub
      call $__nanyx_retain
    end
    local.get $p
  )

  (func $__nanyx_drop_rec_toString_fn_string_to_string (param $p i32)
    ;; (toString: (string -> string))
    local.get $p
    i32.const 8
    i32.sub
    call $__nanyx_release
    if
      local.get $p
      i32.load
      call $__nanyx_drop_fn_string_to_string
      local.get $p
      i32.const 8
      i32.sub
      call $__nanyx_free
    end
  )

  (func $__nanyx_dup_rec_toString_fn_string_to_string (param $p i32) (result i32)
    block $shared
      local.get $p
      i32.const 8
      i32.sub
      call $__nanyx_retain
    end
    local.get $p
  )

  (func $__nanyx_drop_rec_0_word (param $p i32)
    ;; (0: word)
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

  (func $__nanyx_dup_rec_0_word (param $p i32) (result i32)
    block $shared
      local.get $p
      i32.const 8
      i32.sub
      call $__nanyx_retain
    end
    local.get $p
  )
)