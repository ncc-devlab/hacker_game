/* 把一段原始终端字节流喂给 libtsm（godot-xterm 的引擎），
 * 打印渲染出来的屏幕，以及它主动回给客户机的所有应答。
 *
 * 用途：判断「要不要魔改 godot-xterm」时，用真实流量做证据，而不是读源码猜。
 *   用法: tsmdump <cols> <rows> <session.raw>
 *
 * 编译（libtsm 自带的 meson 构建需要 check 测试库，这里直接编源文件绕开）:
 *   git clone --depth 1 https://github.com/lihop/libtsm.git tsm
 *   meson setup tsm/build tsm >/dev/null 2>&1 || true   # 只为生成 config.h
 *   gcc -O1 -o tsmdump tsmdump.c tsm/src/tsm/*.c tsm/src/shared/shl-htable.c \
 *       tsm/external/wcwidth/wcwidth.c \
 *       -Itsm/src/tsm -Itsm/src/shared -Itsm/build -Itsm/external/wcwidth \
 *       -include tsm/build/config.h -lm
 *
 * lihop/libtsm 就是 godot-xterm 的 thirdparty 子模块，所以这里看到的行为
 * 与 godot-xterm 里的 Terminal 节点一致。结论见 docs/终端引擎选型.md。
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <libtsm.h>

static unsigned int g_cols, g_rows;
static uint32_t *g_cells;      /* 每格一个码点，0 表示空 */

/* libtsm 主动写回客户机的字节（DSR 应答、DA 应答等）都从这里出来。
 * godot-xterm 把它接到 data_sent 信号上。 */
static void write_cb(struct tsm_vte *vte, const char *u8, size_t len, void *data)
{
    (void)vte; (void)data;
    fputs("  回写: ", stdout);
    for (size_t i = 0; i < len; i++) {
        unsigned char c = (unsigned char)u8[i];
        if (c == 0x1b) fputs("\\e", stdout);
        else if (c >= 0x20 && c < 0x7f) putchar(c);
        else printf("\\x%02x", c);
    }
    putchar('\n');
}

static int draw_cb(struct tsm_screen *con, uint64_t id, const uint32_t *ch,
                   size_t len, unsigned int width, unsigned int posx,
                   unsigned int posy, const struct tsm_screen_attr *attr,
                   tsm_age_t age, void *data)
{
    (void)con; (void)id; (void)width; (void)attr; (void)age; (void)data;
    if (posx >= g_cols || posy >= g_rows) return 0;
    g_cells[posy * g_cols + posx] = len ? ch[0] : ' ';
    return 0;
}

/* 码点转 UTF-8，这样 DEC 特殊图形被正确映射成制表符时能看出来 */
static void put_cp(uint32_t cp)
{
    if (cp == 0) { putchar(' '); return; }
    if (cp < 0x80) { putchar((int)cp); return; }
    if (cp < 0x800) { putchar(0xC0 | (cp >> 6)); putchar(0x80 | (cp & 0x3F)); return; }
    putchar(0xE0 | (cp >> 12));
    putchar(0x80 | ((cp >> 6) & 0x3F));
    putchar(0x80 | (cp & 0x3F));
}

int main(int argc, char **argv)
{
    if (argc != 4) { fprintf(stderr, "用法: %s <cols> <rows> <raw>\n", argv[0]); return 2; }
    g_cols = (unsigned)atoi(argv[1]);
    g_rows = (unsigned)atoi(argv[2]);

    FILE *f = fopen(argv[3], "rb");
    if (!f) { perror(argv[3]); return 2; }
    fseek(f, 0, SEEK_END); long n = ftell(f); fseek(f, 0, SEEK_SET);
    char *buf = malloc((size_t)n);
    if (fread(buf, 1, (size_t)n, f) != (size_t)n) { perror("read"); return 2; }
    fclose(f);

    struct tsm_screen *screen; struct tsm_vte *vte;
    if (tsm_screen_new(&screen, NULL, NULL)) { fprintf(stderr, "screen_new 失败\n"); return 1; }
    tsm_screen_resize(screen, g_cols, g_rows);
    if (tsm_vte_new(&vte, screen, write_cb, NULL, NULL, NULL)) {
        fprintf(stderr, "vte_new 失败\n"); return 1;
    }

    printf("=== libtsm 主动回给客户机的应答 ===\n");
    tsm_vte_input(vte, buf, (size_t)n);

    g_cells = calloc((size_t)g_cols * g_rows, sizeof(uint32_t));
    tsm_screen_draw(screen, draw_cb, NULL);

    printf("\n=== libtsm 渲染出来的屏幕 (%ux%u) ===\n", g_cols, g_rows);
    for (unsigned y = 0; y < g_rows; y++) {
        putchar('|');
        for (unsigned x = 0; x < g_cols; x++) put_cp(g_cells[y * g_cols + x]);
        printf("|\n");
    }
    return 0;
}
