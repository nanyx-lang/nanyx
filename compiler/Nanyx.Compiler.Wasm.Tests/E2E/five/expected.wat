(module
  (memory 1)
  (export "memory" (memory 0))

  (global $heap_ptr (mut i32) (i32.const 4096))

  (func $__nanyx_alloc (param $size i32) (result i32)
    (local $ptr i32)
    global.get $heap_ptr
    local.set $ptr
    local.get $ptr
    local.get $size
    i32.const 3
    i32.add
    i32.const -4
    i32.and
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
  )
  (func $__nanyx_tag_box (param $code i32) (param $payload i32) (result i32)
    (local $cell i32)
    i32.const 8
    call $__nanyx_alloc
    local.tee $cell
    local.get $code
    i32.store
    local.get $cell
    local.get $payload
    i32.store offset=4
    local.get $cell
  )
  (func $__nanyx_tag_code (param $tag i32) (result i32)
    local.get $tag
    i32.const 1
    i32.and
    if (result i32)
      local.get $tag
    else
      local.get $tag
      i32.load
    end
  )

  (func $getOrDefault (param $opt i32) (param $default i32) (result i32) (local $__match_tmp_0 i32) (local $__payload_value i32)
    local.get $opt
    local.set $__match_tmp_0
    local.get $__match_tmp_0
      call $__nanyx_tag_code
      i32.const 5
      i32.eq
    (if (result i32)
      (then
        local.get $__match_tmp_0
      i32.load offset=4
        local.set $__payload_value
    local.get $__payload_value
      )
      (else
        local.get $__match_tmp_0
      call $__nanyx_tag_code
      i32.const 3
      i32.eq
    (if (result i32)
      (then
        local.get $default
      )
      (else
        unreachable
      )
    )
      )
    )
  )
  (export "getOrDefault" (func $getOrDefault))

  (func $main (result i32) (local $x i32) (local $y i32) (local $a i32) (local $b i32)
    i32.const 5
    i32.const 42
    call $__nanyx_tag_box
    local.set $x
    i32.const 3
    local.set $y
    local.get $x
    i32.const 0
    call $getOrDefault
    local.set $a
    local.get $y
    i32.const 99
    call $getOrDefault
    local.set $b
    local.get $a
    local.get $b
    i32.add
  )
  (export "main" (func $main))
)