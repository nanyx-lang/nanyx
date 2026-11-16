(module
  (memory 1)
  (export "memory" (memory 0))

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
  (func $__nanyx_new_cell (param $size i32) (result i32)
    (local $block i32)
    local.get $size
    i32.const 4
    i32.add
    call $__nanyx_alloc
    local.tee $block
    i32.const 1
    i32.store
    local.get $block
    i32.const 4
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
  (func $__nanyx_tag_box_i64 (param $code i32) (param $payload i64) (result i32)
    (local $cell i32)
    i32.const 12
    call $__nanyx_new_cell
    local.tee $cell
    local.get $code
    i32.store
    local.get $cell
    local.get $payload
    i64.store offset=4
    local.get $cell
  )

  (global $main__built (mut i32) (i32.const 0))
  (global $main__value (mut i64) (i64.const 0))

  (func $classify__tags_ (param $value i32) (result i64) (local $__owned_0 i32)
    local.get $value
    i32.const 3
    i64.const 3
    call $__nanyx_tag_box_i64
    local.tee $__owned_0
    call $__nanyx_equals_tags_some_int
    local.get $__owned_0
    call $__nanyx_drop_tags_some_int
    (if (result i64)
      (then
        i64.const 1
      )
      (else
        i64.const 0
      )
    )
  )

  (func $main__build (result i64) (local $__inlined2 i32) (local $__owned_0 i32)
    i32.const 3
    i64.const 3
    call $__nanyx_tag_box_i64
    local.set $__inlined2
    local.get $__inlined2
    i32.const 3
    i64.const 3
    call $__nanyx_tag_box_i64
    local.tee $__owned_0
    call $__nanyx_equals_tags_some_int
    local.get $__inlined2
    call $__nanyx_drop_tags_some_int
    local.get $__owned_0
    call $__nanyx_drop_tags_some_int
    (if (result i64)
      (then
        i64.const 1
      )
      (else
        i64.const 0
      )
    )
  )

  (func $main (result i64)
    global.get $main__built
    i32.eqz
    if
      call $main__build
      global.set $main__value
      i32.const 1
      global.set $main__built
    end
    global.get $main__value
  )
  (export "main" (func $main))

  (func $__nanyx_drop_tags_some_int (param $p i32)
    ;; #some(int)
    local.get $p
    i32.const 1
    i32.and
    if
      return
    end
    local.get $p
    i32.const 4
    i32.sub
    call $__nanyx_release
    if
      local.get $p
      i32.const 4
      i32.sub
      call $__nanyx_free
    end
  )

  (func $__nanyx_dup_tags_some_int (param $p i32) (result i32)
    block $shared
      local.get $p
      i32.const 1
      i32.and
      br_if $shared
      local.get $p
      i32.const 4
      i32.sub
      call $__nanyx_retain
    end
    local.get $p
  )

  (func $__nanyx_equals_tags_some_int (param $a i32) (param $b i32) (result i32)
    ;; #some(int)
    local.get $a
    local.get $b
    i32.or
    i32.const 1
    i32.and
    if
      local.get $a
      local.get $b
      i32.eq
      return
    end
    local.get $a
    i32.load
    local.get $b
    i32.load
    i32.ne
    if
      i32.const 0
      return
    end
    local.get $a
    i64.load offset=4
    local.get $b
    i64.load offset=4
    i64.eq
  )
)
