# Isolamento do controle na sessão Apollo

A overlay precisa funcionar tanto com controles conectados diretamente ao PC quanto em uma sessão Apollo. Hoje ela lê XInput e não impede que outro processo leia o mesmo dispositivo. O aviso na interface descreve esse estado. Foco, privilégios administrativos e a espera pela soltura dos botões não estabelecem exclusividade.

## Caminho independente do Apollo

Uma possível arquitetura para o PC é um serviço que recebe cada controle de origem e publica um controle virtual separado para o jogo. O serviço entrega o estado original à overlay e envia estado neutro ao jogo enquanto houver isolamento confirmado. Para evitar entrada duplicada, o jogo também precisa deixar de acessar a origem. Isso exige integração com o sistema de dispositivos, permissões e configuração antes de o jogo abrir os dispositivos; deve preservar as configurações existentes e oferecer recuperação se o serviço parar.

Essa arquitetura ainda precisa ser implementada e validada. Sua viabilidade depende do tipo de controle, dos drivers e de como o jogo lê a entrada. A leitura de Guide, Bluetooth e USB, mudanças de slot, múltiplos controles, controles virtuais do Apollo, Steam Input e anti-cheat precisam ser verificados. A overlay deve continuar utilizável mesmo quando o serviço não estiver instalado, indicando que o controle é compartilhado. Não se deve declarar isolamento enquanto a origem não estiver efetivamente inacessível ao jogo e o estado neutro do controle apresentado ao jogo não estiver confirmado.

## Integração a validar

Na revisão pública `adc5c5a0bd80831ce495434bb16aee2cd4175fb8`, o Apollo cria controles virtuais pelo ViGEm e encaminha os estados recebidos a `platf::gamepad_update`. A overlay lê esse mesmo controle virtual. Neutralizá-lo sem outro canal também impediria a overlay de receber navegação e Guide.

Para esse backend específico, uma integração precisa de alterações no Apollo instalado e de um canal separado para a overlay receber o estado original de cada controle. O componente que é dono do controle virtual deve, enquanto o isolamento estiver ativo, encaminhar o estado original à overlay e enviar um estado neutro ao controle exposto ao Windows. A decisão deve abranger todos os envios, incluindo tarefas atrasadas, desconexão e Guide, e ser aplicada no ponto de escrita comum ao dispositivo. Esse backend é opcional e não pode ser requisito para usar a overlay no computador.

Fontes para a revisão consultada:

- [Criação de controles no Windows](https://github.com/ClassicOldSong/Apollo/blob/adc5c5a0bd80831ce495434bb16aee2cd4175fb8/src/platform/windows/input.cpp#L198-L266).
- [Encaminhamento e tarefas de input](https://github.com/ClassicOldSong/Apollo/blob/adc5c5a0bd80831ce495434bb16aee2cd4175fb8/src/input.cpp#L1124-L1193).
- [Escrita do Guide no controle virtual](https://github.com/ClassicOldSong/Apollo/blob/adc5c5a0bd80831ce495434bb16aee2cd4175fb8/src/platform/windows/input.cpp#L1278).

O canal deve autenticar a sessão e o processo autorizado, identificar controles e conexões por época e informar explicitamente quando o Apollo confirma a neutralização. A interface só poderá afirmar que o controle está isolado após essa confirmação. A ausência, uma versão incompatível ou a perda do canal deve ser apresentada como isolamento indisponível.

A liberação precisa continuar neutralizando o controle público até observar todas as entradas soltas por um intervalo estável. O protocolo precisa tratar nova entrada durante a transição, desconexão e reconexão, entrada atrasada, encerramento da overlay e falha do próprio Apollo. Um mecanismo de expiração deve impedir que uma overlay encerrada deixe o controle permanentemente bloqueado, sem transformar uma leitura desconhecida em prova de soltura. A política de recuperação exige validação com hardware e indicação explícita ao usuário.

## Limites e verificação

Esta integração ainda não está implementada. Versão, configuração e driver instalados no computador do usuário precisam ser identificados antes de escolher uma implementação. Outros controles físicos ou virtuais e outros caminhos de input não são cobertos automaticamente. Steam, Game Bar e outros consumidores do Guide também precisam ser avaliados.

HidHide pode ajudar em algumas configurações, mas ocultar dinamicamente um HID não comprova a revogação de acessos já abertos nem a supressão de todos os caminhos XInput. Não altere sua configuração global automaticamente nem afirme isolamento sem observação do dispositivo e dos consumidores. Consulte o [guia oficial](https://docs.nefarius.at/projects/HidHide/Simple-Setup-Guide/).

Antes de declarar suporte a uma configuração, execute no Windows testes que observem simultaneamente o estado original recebido pela overlay e o estado lido por um processo separado. Cubra abertura e fechamento com cada botão, sticks e gatilhos ativos, Guide, leituras em segundo plano, múltiplos controles, reconexão, perda de IPC, encerramento inesperado, reinício do Apollo e coexistência com Steam/Game Bar. Repita na sessão real Apollo para TV e registre a versão do Apollo, do driver e do cliente.
