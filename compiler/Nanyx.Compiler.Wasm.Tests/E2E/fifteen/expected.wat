(module
  (memory 1)
  (export "memory" (memory 0))

  (global $heap_ptr (mut i32) (i32.const 4356))

  (func $__nanyx_free (param $ptr i32)
    (local $size i32)
    (local $head i32)
    (local $next i32)
    (local $before i32)
    (local $joined i32)
    local.get $ptr
    i32.const 4
    i32.sub
    i32.load
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
    local.set $head
    local.get $size
    i32.const 256
    i32.le_u
    if
      local.get $ptr
      local.get $head
      i32.load
      i32.store
      local.get $head
      local.get $ptr
      i32.store
      return
    end
    block $found
      loop $search
        local.get $head
        i32.load
        local.tee $next
        i32.eqz
        br_if $found
        local.get $next
        local.get $ptr
        i32.gt_u
        br_if $found
        local.get $head
        local.set $before
        local.get $next
        local.set $head
        br $search
      end
    end
    local.get $ptr
    local.get $size
    i32.add
    i32.const 4
    i32.add
    local.get $next
    i32.eq
    if
      local.get $size
      local.get $next
      i32.const 4
      i32.sub
      i32.load
      i32.const -4
      i32.and
      i32.add
      i32.const 4
      i32.add
      local.set $size
      local.get $next
      i32.load
      local.set $next
    end
    local.get $before
    if
      local.get $head
      local.get $head
      i32.const 4
      i32.sub
      i32.load
      i32.const -4
      i32.and
      local.tee $joined
      i32.add
      i32.const 4
      i32.add
      local.get $ptr
      i32.eq
      if
        local.get $joined
        local.get $size
        i32.add
        i32.const 4
        i32.add
        local.set $size
        local.get $head
        local.set $ptr
        local.get $before
        local.set $head
      end
    end
    local.get $ptr
    local.get $size
    i32.add
    global.get $heap_ptr
    i32.eq
    if
      local.get $head
      i32.const 0
      i32.store
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
    i32.store
    local.get $ptr
    local.get $next
    i32.store
    local.get $head
    local.get $ptr
    i32.store
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
  (func $__nanyx_int_add (param $a i64) (param $b i64) (result i64)
    (local $r i64)
    local.get $a
    local.get $b
    i64.add
    local.set $r
    local.get $a
    local.get $r
    i64.xor
    local.get $b
    local.get $r
    i64.xor
    i64.and
    i64.const 0
    i64.lt_s
    if
      i64.const -9223372036854775808
      i64.const -1
      i64.div_s
      drop
    end
    local.get $r
  )

  (func $main (result i64) (local $__inlined5_i64 i64)
    i64.const 42
    local.set $__inlined5_i64
    local.get $__inlined5_i64
    i64.const 8
    call $__nanyx_int_add
  )
  (export "main" (func $main))

  (func $__lambda_1 (param $__env i32) (param $a_i64 i64) (param $b_i64 i64) (result i64)
    local.get $a_i64
    local.get $b_i64
    call $__nanyx_int_add
  )
  (export "__lambda_1" (func $__lambda_1))

  (func $__lambda_3 (param $__env i32) (result i64)
    i64.const 42
  )
  (export "__lambda_3" (func $__lambda_3))

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
