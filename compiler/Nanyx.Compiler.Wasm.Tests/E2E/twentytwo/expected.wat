(module
  (import "wasi_snapshot_preview1" "fd_write" (func $fd_write (param i32 i32 i32 i32) (result i32)))
  (memory 1)
  (export "memory" (memory 0))
  (data (i32.const 224) "\0a")
  (func $dbg_str (param $location i32) (param $text i32)
    i32.const 0
    local.get $location
    i32.store
    i32.const 4
    local.get $location
    i32.const 4
    i32.sub
    i32.load
    i32.store
    i32.const 2
    i32.const 0
    i32.const 1
    i32.const 8
    call $fd_write
    drop
    i32.const 0
    local.get $text
    i32.store
    i32.const 4
    local.get $text
    i32.const 4
    i32.sub
    i32.load
    i32.store
    i32.const 2
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
    i32.const 2
    i32.const 0
    i32.const 1
    i32.const 8
    call $fd_write
    drop
  )

  (global $heap_ptr (mut i32) (i32.const 4356))

  (func $__nanyx_alloc (param $size i32) (result i32)
    (local $link i32)
    (local $ptr i32)
    (local $found i32)
    (local $rest i32)
    (local $next i32)
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
    i32.const 256
    i32.le_u
    if (result i32)
      i32.const 4092
      local.get $size
      i32.add
    else
      i32.const 4352
    end
    local.set $link
    local.get $size
    i32.const 256
    i32.le_u
    if
      local.get $link
      i32.load
      local.tee $ptr
      if
        local.get $link
        local.get $ptr
        i32.load
        i32.store
        local.get $ptr
        return
      end
    else
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
          i32.const -4
          i32.and
          local.tee $found
          local.get $size
          i32.ge_u
          if
            local.get $found
            local.get $size
            i32.sub
            i32.const 264
            i32.ge_u
            if
              local.get $ptr
              local.get $size
              i32.add
              i32.const 4
              i32.add
              local.tee $rest
              i32.const 4
              i32.sub
              local.get $found
              local.get $size
              i32.sub
              i32.const 4
              i32.sub
              local.tee $next
              i32.const 1
              i32.or
              i32.store
              local.get $rest
              local.get $next
              i32.add
              i32.const 4
              i32.sub
              local.get $next
              i32.store
              local.get $rest
              local.get $ptr
              i32.load
              local.tee $next
              i32.store
              local.get $rest
              local.get $link
              i32.store offset=4
              local.get $link
              local.get $rest
              i32.store
              local.get $next
              if
                local.get $next
                local.get $rest
                i32.store offset=4
              end
              local.get $size
              local.set $found
            else
              local.get $ptr
              i32.load offset=4
              local.get $ptr
              i32.load
              i32.store
              local.get $ptr
              i32.load
              local.tee $next
              if
                local.get $next
                local.get $ptr
                i32.load offset=4
                i32.store offset=4
              end
              local.get $ptr
              local.get $found
              i32.add
              local.get $ptr
              local.get $found
              i32.add
              i32.load
              i32.const -3
              i32.and
              i32.store
            end
            local.get $ptr
            i32.const 4
            i32.sub
            local.get $found
            i32.store
            local.get $ptr
            return
          end
          local.get $ptr
          local.set $link
          br $search
        end
      end
    end
    global.get $heap_ptr
    i32.const 4
    i32.add
    local.tee $ptr
    local.get $size
    i32.add
    local.tee $rest
    local.get $ptr
    i32.lt_u
    if
      unreachable
    end
    local.get $rest
    global.set $heap_ptr
    global.get $heap_ptr
    i64.extend_i32_u
    i64.const 65535
    i64.add
    i64.const 16
    i64.shr_u
    i32.wrap_i64
    memory.size
    i32.sub
    local.tee $rest
    i32.const 0
    i32.gt_s
    if
      local.get $rest
      memory.grow
      i32.const -1
      i32.eq
      if
        unreachable
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
    (local $header i32)
    (local $size i32)
    (local $next i32)
    (local $joined i32)
    (local $link i32)
    local.get $ptr
    i32.const 4
    i32.sub
    i32.load
    local.tee $header
    i32.const -4
    i32.and
    local.set $size
    local.get $size
    i32.const 256
    i32.le_u
    if (result i32)
      i32.const 4092
      local.get $size
      i32.add
    else
      i32.const 4352
    end
    local.set $next
    local.get $size
    i32.const 256
    i32.le_u
    if
      local.get $ptr
      local.get $next
      i32.load
      i32.store
      local.get $next
      local.get $ptr
      i32.store
      return
    end
    local.get $ptr
    local.get $size
    i32.add
    global.get $heap_ptr
    i32.ne
    if
      local.get $ptr
      local.get $size
      i32.add
      i32.const 4
      i32.add
      local.tee $next
      i32.const 4
      i32.sub
      i32.load
      local.tee $joined
      i32.const 1
      i32.and
      local.get $joined
      i32.const -4
      i32.and
      local.tee $joined
      i32.const 256
      i32.gt_u
      i32.and
      if
        local.get $next
        i32.load offset=4
        local.get $next
        i32.load
        i32.store
        local.get $next
        i32.load
        local.tee $link
        if
          local.get $link
          local.get $next
          i32.load offset=4
          i32.store offset=4
        end
        local.get $size
        local.get $joined
        i32.add
        i32.const 4
        i32.add
        local.set $size
      end
    end
    local.get $header
    i32.const 2
    i32.and
    if
      local.get $ptr
      i32.const 4
      i32.sub
      local.get $ptr
      i32.const 8
      i32.sub
      i32.load
      local.tee $joined
      i32.sub
      local.set $ptr
      local.get $ptr
      i32.load offset=4
      local.get $ptr
      i32.load
      i32.store
      local.get $ptr
      i32.load
      local.tee $link
      if
        local.get $link
        local.get $ptr
        i32.load offset=4
        i32.store offset=4
      end
      local.get $size
      local.get $joined
      i32.add
      i32.const 4
      i32.add
      local.set $size
    end
    local.get $ptr
    local.get $size
    i32.add
    global.get $heap_ptr
    i32.eq
    if
      local.get $ptr
      i32.const 4
      i32.sub
      global.set $heap_ptr
      return
    end
    local.get $ptr
    i32.const 4
    i32.sub
    local.get $size
    i32.const 1
    i32.or
    i32.store
    local.get $ptr
    local.get $size
    i32.add
    local.tee $next
    i32.const 4
    i32.sub
    local.get $size
    i32.store
    local.get $next
    local.get $next
    i32.load
    i32.const 2
    i32.or
    i32.store
    local.get $ptr
    i32.const 4352
    i32.load
    local.tee $next
    i32.store
    local.get $ptr
    i32.const 4352
    i32.store offset=4
    local.get $next
    if
      local.get $next
      local.get $ptr
      i32.store offset=4
    end
    i32.const 4352
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
  (func $__nanyx_int_text_length (param $n i64) (param $signed i32) (result i32)
    (local $len i32)
    i32.const 1
    local.set $len
    local.get $signed
    if
      local.get $n
      i64.const 0
      i64.lt_s
      if
        i32.const 2
        local.set $len
        i64.const 0
        local.get $n
        i64.sub
        local.set $n
      end
    end
    block $counted
      loop $count
        local.get $n
        i64.const 10
        i64.lt_u
        br_if $counted
        local.get $len
        i32.const 1
        i32.add
        local.set $len
        local.get $n
        i64.const 10
        i64.div_u
        local.set $n
        br $count
      end
    end
    local.get $len
  )
  (func $__nanyx_int_text_write (param $at i32) (param $n i64) (param $signed i32) (result i32)
    (local $end i32)
    local.get $signed
    if
      local.get $n
      i64.const 0
      i64.lt_s
      if
        local.get $at
        i32.const 45
        i32.store8
        local.get $at
        i32.const 1
        i32.add
        local.set $at
        i64.const 0
        local.get $n
        i64.sub
        local.set $n
      end
    end
    local.get $at
    local.get $n
    i32.const 0
    call $__nanyx_int_text_length
    i32.add
    local.tee $end
    local.set $at
    loop $digit
      local.get $at
      i32.const 1
      i32.sub
      local.tee $at
      local.get $n
      i64.const 10
      i64.rem_u
      i32.wrap_i64
      i32.const 48
      i32.add
      i32.store8
      local.get $n
      i64.const 10
      i64.div_u
      local.tee $n
      i64.const 0
      i64.ne
      br_if $digit
    end
    local.get $end
  )

  (data (i32.const 256) "\00\00\00\00\00\00\00\00\00\00\00\00")
  (data (i32.const 268) "\00\00\00\00\04\00\00\00[2] \00\00\00\00")
  (data (i32.const 284) "\00\00\00\00\04\00\00\00[3] \00\00\00\00")
  (data (i32.const 300) "\00\00\00\00\0b\00\00\00Hello world\00")

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

  (func $__nanyx_start (local $__dbg_value__in1__in1__in3_i64 i64) (local $__interp_len i32) (local $__interp_base i32) (local $__interp_cursor i32) (local $__interp_ptr i32) (local $__interp_copy_len i32) (local $__owned_0 i32) (local $__owned_1 i32) (local $__owned_2 i32)
    i64.const 42
    local.set $__dbg_value__in1__in1__in3_i64
    i32.const 276
    i32.const 0
    local.set $__interp_len
    local.get $__dbg_value__in1__in1__in3_i64
    i32.const 1
    call $__nanyx_int_text_length
    local.get $__interp_len
    i32.add
    local.set $__interp_len
    local.get $__interp_len
    call $__nanyx_string_alloc
    local.tee $__interp_base
    local.set $__interp_cursor
    local.get $__interp_cursor
    local.get $__dbg_value__in1__in1__in3_i64
    i32.const 1
    call $__nanyx_int_text_write
    local.set $__interp_cursor
    local.get $__interp_base
    local.tee $__owned_0
    call $dbg_str
    i32.const 0
    local.get $__owned_0
    call $__nanyx_drop_string
    drop
    i32.const 292
    i32.const 308
    local.tee $__owned_2
    call $__nanyx_string_quote
    local.get $__owned_2
    call $__nanyx_drop_string
    local.tee $__owned_1
    call $dbg_str
    i32.const 0
    local.get $__owned_1
    call $__nanyx_drop_string
    drop
    i32.const 0
    drop
  )

  (start $__nanyx_start)
)
